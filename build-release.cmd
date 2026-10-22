@echo off
setlocal
cd /d "%~dp0"

echo Building FatxMount (Release, self-contained single file)...
dotnet publish FatxMount.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see the errors above.
  pause
  exit /b 1
)

echo.
echo Done. Your program is: %~dp0publish\FatxMount.exe
echo It needs the Dokany 2.x driver installed on the PC where it runs.
pause
