@echo off
chcp 65001 >nul
title ai-town API server
cd /d %~dp0
echo [ai-town] Starting API server (port 8765)...
python ai_town_server.py
pause
