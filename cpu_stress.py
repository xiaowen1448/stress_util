#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from __future__ import print_function
import argparse
import multiprocessing
import threading
import time
import os
import math
import sys

# =========================
# 系统环境检测
# =========================
if sys.platform.startswith('win'):
    print("错误：此工具仅支持 Linux 环境")
    print("Windows 系统无法访问 Linux 特有的系统文件（如 /proc/cpuinfo, /sys/class/thermal/）")
    print("请在 Linux 系统下运行此工具")
    sys.exit(1)

# =========================
# Help 对齐
# =========================
class AlignedHelpFormatter(argparse.HelpFormatter):
    def __init__(self, *args, **kwargs):
        kwargs['max_help_position'] = 32
        kwargs['width'] = 100
        # Python 2.7 必须使用显式 super(Class, self)，且自定义 formatter 在 Py2 下可能触发 argparse metavar 错误
        argparse.HelpFormatter.__init__(self, *args, **kwargs)

# =========================
# 系统信息
# =========================
class SystemInfo(object):

    @staticmethod
    def get_cpu_info():
        cpu_info = {
            'cores': multiprocessing.cpu_count(),
            'model': 'Unknown',
            'arch': 'Unknown'
        }

        if os.path.exists('/proc/cpuinfo'):
            with open('/proc/cpuinfo') as f:
                for line in f:
                    if 'model name' in line:
                        cpu_info['model'] = line.split(':', 1)[1].strip()
                        break

        if os.path.exists('/proc/cpuinfo'):
            with open('/proc/cpuinfo') as f:
                for line in f:
                    if 'vendor_id' in line:
                        vendor = line.split(':', 1)[1].strip()
                        if 'Intel' in vendor:
                            cpu_info['arch'] = 'x86_64 (Intel)'
                        elif 'AMD' in vendor:
                            cpu_info['arch'] = 'x86_64 (AMD)'
                        else:
                            cpu_info['arch'] = vendor
                        break

        return cpu_info

# =========================
# CPU 监控（频率 / 温度 / 利用率）
# =========================
class CPUMonitor(object):

    def __init__(self, duration):
        self.duration = duration
        self.running = True
        self.data = {
            'temps': [],
            'utils': []
        }

    def get_temp(self):
        path = '/sys/class/thermal/thermal_zone0/temp'
        if os.path.exists(path):
            with open(path) as f:
                return int(f.read().strip()) / 1000.0
        return 0

    def get_util(self):
        try:
            with open('/proc/stat') as f:
                a = f.readline().split()
            time.sleep(0.1)
            with open('/proc/stat') as f:
                b = f.readline().split()

            idle = float(b[4]) - float(a[4])
            total = sum(map(float, b[1:8])) - sum(map(float, a[1:8]))
            return 100.0 * (1 - idle / total) if total > 0 else 0
        except Exception:
            return 0

    def loop(self):
        start = time.time()
        while self.running and time.time() - start < self.duration:
            t = self.get_temp()
            u = self.get_util()
            if t > 0:
                self.data['temps'].append(t)
            if u > 0:
                self.data['utils'].append(u)
            time.sleep(0.5)

    def start(self):
        t = threading.Thread(target=self.loop)
        t.daemon = True
        t.start()

    def stop(self):
        self.running = False
        time.sleep(0.5)

        return {
            'avg_temp': sum(self.data['temps']) / len(self.data['temps']) if self.data['temps'] else 0,
            'max_temp': max(self.data['temps']) if self.data['temps'] else 0,
            'min_temp': min(self.data['temps']) if self.data['temps'] else 0,
            'avg_util': sum(self.data['utils']) / len(self.data['utils']) if self.data['utils'] else 0
        }

# =========================
# CPU 压力测试
# =========================
def _stress_worker(duration, percent):
    """模块级 worker，供 multiprocessing.Pool 调用（Python 2.7 无法 pickle 类上的 staticmethod）。"""
    end = time.time() + duration
    ops = 0
    work = percent / 100.0
    sleep = (100 - percent) / 100.0

    while time.time() < end:
        s = time.time()
        while time.time() - s < work:
            for i in range(200):
                math.sqrt(i * i + 1)
                ops += 1
        if sleep > 0:
            time.sleep(sleep)

    return ops


class CPUStressTest(object):

    def __init__(self, threads, duration, percent):
        self.threads = threads
        self.duration = duration
        self.percent = percent

    def run(self, cpu_info):
        start_ts = time.time()
        start_str = time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(start_ts))

        print("\n========== CPU 压力测试 ==========")
        print("CPU 型号:       {}".format(cpu_info['model']))
        print("CPU 架构:       {}".format(cpu_info['arch']))
        print("逻辑核心数:     {}".format(cpu_info['cores']))
        print("测试核心数:     {}".format(self.threads))
        print("CPU 使用率:     {}%".format(self.percent))
        print("开始时间:       {}".format(start_str))

        monitor = CPUMonitor(self.duration)
        monitor.start()

        pool = multiprocessing.Pool(self.threads)
        results = [
            pool.apply_async(_stress_worker, (self.duration, self.percent))
            for _ in range(self.threads)
        ]
        pool.close()
        pool.join()

        ops = sum(r.get() for r in results)
        stat = monitor.stop()

        end_ts = time.time()
        end_str = time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(end_ts))
        elapsed = end_ts - start_ts

        print("\n========== 测试结果 ==========")
        print("结束时间:       {}".format(end_str))
        print("持续时长:       {:.2f} 秒".format(elapsed))

        print("\n【CPU 温度监控】")
        if stat['avg_temp'] > 0:
            print("平均温度:       {:.1f} °C".format(stat['avg_temp']))
            print("最高温度:       {:.1f} °C".format(stat['max_temp']))
            print("最低温度:       {:.1f} °C".format(stat['min_temp']))
        else:
            print("温度信息:       未获取（虚拟机 / 无 thermal_zone）")

        print("\n【CPU 性能】")
        print("总操作数:       {:,}".format(ops))
        print("平均吞吐量:     {:,.0f} ops/s".format(ops / elapsed))
        print("平均 CPU 利用率:{:.1f}%".format(stat['avg_util']))

# =========================
# 主入口
# =========================
def main():
    # Python 2.7 下使用自定义 formatter_class 会在添加默认 --help 时触发 ValueError: length of metavar tuple does not match nargs，故仅在 Python 3 下使用
    use_formatter = (sys.version_info[0] >= 3)
    parser = argparse.ArgumentParser(
        description='Linux CPU 压力测试工具（含温度监控）',
        formatter_class=AlignedHelpFormatter if use_formatter else argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument('--cpu', action='store_true', help='运行 CPU 压力测试')
    parser.add_argument('--duration', type=int, default=60, help='测试时长(秒)')
    parser.add_argument('--cpu-threads', type=int, help='CPU 测试线程数')
    parser.add_argument('--cpu-percent', type=int, default=100, help='CPU 使用率(1-100)')

    args = parser.parse_args()

    if not args.cpu:
        parser.print_help()
        return

    cpu_info = SystemInfo.get_cpu_info()
    threads = args.cpu_threads or cpu_info['cores']

    test = CPUStressTest(threads, args.duration, args.cpu_percent)
    test.run(cpu_info)

if __name__ == '__main__':
    main()
