@echo off
rem Creates the local Ollama model the app expects (default name: qwen3-vl:4b-cctv).
rem   tools\setup-model.bat              -> qwen3-vl:4b-cctv
rem   tools\setup-model.bat my-model     -> a different name (then pick it in Settings)
setlocal
set "NAME=%~1"
if "%NAME%"=="" set "NAME=qwen3-vl:4b-cctv"

where ollama >nul 2>&1
if errorlevel 1 (
  echo ollama was not found. Install it from https://ollama.com and run this script again.
  exit /b 1
)
ollama list >nul 2>&1
if errorlevel 1 (
  echo Cannot reach the Ollama server. Start the Ollama app and run this script again.
  exit /b 1
)

echo Downloading the base model qwen3-vl:4b (a few GB the first time)...
ollama pull qwen3-vl:4b
if errorlevel 1 exit /b 1
echo Creating %NAME% ...
ollama create "%NAME%" -f "%~dp0Modelfile.qwen3-vl-4b-cctv"
if errorlevel 1 exit /b 1
echo Done. Start the app and choose "%NAME%" under Settings ^> Model.