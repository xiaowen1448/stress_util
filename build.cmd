@echo off
setlocal

REM 目标：.NET Framework 4.8（兼容 Windows Server 2012 / 2008 R2 SP1 及更新系统）。
REM 产物为框架依赖 exe+dll，目标机只需装好 .NET Framework 4.8 运行时即可运行。
REM 用法示例：
REM   build.cmd
REM   build.cmd -Clean
REM   build.cmd -Configuration Debug

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
