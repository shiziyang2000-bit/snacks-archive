@echo off
setlocal
REM Build and export a LOCAL archive-edition image. No registry login or push.
set VERSION=2.20.4
set IMAGE=snacks-archive:%VERSION%
docker buildx build --tag %IMAGE% --load -f Snacks/Dockerfile .
if errorlevel 1 exit /b 1
docker save --output snacks-archive-v%VERSION%.tar %IMAGE%
if errorlevel 1 exit /b 1
echo Exported snacks-archive-v%VERSION%.tar locally. No image was published.
