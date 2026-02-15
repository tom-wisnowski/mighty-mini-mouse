@echo off
echo === Building BtInputInterceptor ===
dotnet build BtInputInterceptor\src\BtInputInterceptor.csproj
if %ERRORLEVEL% neq 0 (
    echo Build failed!
    exit /b %ERRORLEVEL%
)
echo === Running BtInputInterceptor ===
dotnet run --project BtInputInterceptor\src\BtInputInterceptor.csproj
