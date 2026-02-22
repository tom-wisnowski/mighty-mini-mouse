@echo off
echo === Building MightyMiniMouse ===
dotnet build MightyMiniMouse\src\MightyMiniMouse.csproj
if %ERRORLEVEL% neq 0 (
    echo Build failed!
    exit /b %ERRORLEVEL%
)
echo === Running MightyMiniMouse ===
dotnet run --project MightyMiniMouse\src\MightyMiniMouse.csproj
