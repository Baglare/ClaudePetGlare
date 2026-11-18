@echo off
setlocal
set "PETGLARE_EXE=%~dp0Petglare.exe"
if not exist "%PETGLARE_EXE%" set "PETGLARE_EXE=%~dp0Teslim\Petglare.exe"
if not exist "%PETGLARE_EXE%" (
    echo Petglare.exe bulunamadi. Yayin ZIP dosyasini tamamen cikarin veya projeyi derleyin.
    pause
    exit /b 1
)
start "" "%PETGLARE_EXE%" --setup
