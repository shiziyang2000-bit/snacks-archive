@echo off
setlocal
REM Local development image only. No registry login or push.
docker buildx build --tag snacks-archive:dev --load -f Snacks/Dockerfile .
if errorlevel 1 exit /b 1
echo Built local snacks-archive:dev. No image was published.
