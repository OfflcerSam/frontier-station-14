@echo off
setlocal EnableDelayedExpansion

set "ROOT=D:\ServerFiles\SS14\FrontierStation14\FS14Main\frontier-station-14"
set "DATADIR=%ROOT%\bin\Content.Server\data"
set "OUTDIR=%DATADIR%\imageoutput"

cd /d "%ROOT%"

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set FILES=
set COUNT=0

for %%F in ("%DATADIR%\*.yml") do (
    echo Found: %%~nxF
    set FILES=!FILES! "bin\Content.Server\data\%%~nxF"
    set /a COUNT+=1
)

if !COUNT! EQU 0 (
    echo No .yml files found in %DATADIR%.
    pause
    exit /b 1
)

echo.
echo Rendering !COUNT! file^(s^)...
echo.

dotnet run --project Content.MapRenderer -- --files !FILES! --output "%OUTDIR%"

echo.
echo Exit code: %ERRORLEVEL%
pause