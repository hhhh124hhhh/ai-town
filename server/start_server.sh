#!/bin/bash
# 启动民国小镇 AI 后端（http://127.0.0.1:8765）
cd "$(dirname "$0")"
exec python3 ai_town_server.py
