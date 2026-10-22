@echo off
rem Builds the Numidium bodies and outfits into the SkyrimDev test copy with BodySlide.
rem Groups "Numidium Female" / "Numidium Male" are in BodySlide\SliderGroups\Numidium.xml
rem and list exactly the variants we use (CBBE Underwear body, HIMBO Vanilla Nevernude body).
rem --trimorphs builds the morph files RaceMenu's body sliders need.

set DATA=E:\Jarldom\SkyrimDev\Data
set BODYSLIDE=%DATA%\CalienteTools\BodySlide

tasklist /FI "IMAGENAME eq BodySlide.exe" | find /I "BodySlide.exe" >nul && (
  echo BodySlide is already open. Close it first, then run this again.
  pause
  exit /b 1
)

pushd "%BODYSLIDE%"

echo Building female bodies and outfits (CBBE Vanilla preset)...
BodySlide.exe --groupbuild "Numidium Female" --preset "CBBE Vanilla" --targetdir "%DATA%" --trimorphs
echo   finished with code %ERRORLEVEL%

echo Building male bodies and outfits (HIMBO Default preset)...
BodySlide.exe --groupbuild "Numidium Male" --preset "HIMBO Default" --targetdir "%DATA%" --trimorphs
echo   finished with code %ERRORLEVEL%

popd
echo.
echo Done. Tell Claude it finished so the results can be checked.
pause
