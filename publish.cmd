@echo off
echo === Publishing MightyMiniMouse ===
dotnet publish MightyMiniMouse\src\MightyMiniMouse.csproj -c Release -o publish --self-contained true %*
if %ERRORLEVEL% neq 0 (
    echo Publish failed!
    exit /b %ERRORLEVEL%
)
echo === Publish successful ===
