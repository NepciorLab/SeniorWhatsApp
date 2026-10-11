@echo off
rem Buduje samodzielny, jednoplikowy SeniorWhatsApp.exe (Windows x64) do folderu nadrzednego.
rem Wymaga .NET 8 SDK. Gotowy plik nie wymaga instalowania .NET.
cd /d "%~dp0Source"
dotnet publish SeniorWhatsApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "%~dp0."
pause
