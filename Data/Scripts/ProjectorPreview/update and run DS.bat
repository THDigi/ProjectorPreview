@echo off
cls
rmdir /S /Q obj
rmdir /S /Q bin

cd C:\Steam\steamapps\common\SpaceEngineers\Bin64\
start /WAIT sewt -m ProjectorPreview.dev --upload

cd C:\Steam\steamapps\common\SpaceEngineers\DedicatedServer64\
start SpaceEngineersDedicated.exe -console

timeout /T 10
start steam://connect/127.0.0.1:27016