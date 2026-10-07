@echo off
cd /d D:\Projects\FreeCoding\live-qs\server
docker compose up -d >> D:\Projects\FreeCoding\live-qs\.scratch\live-qs-v1estore.log 2>&1
echo %date% %time% restore-executed >> D:\Projects\FreeCoding\live-qs\.scratch\live-qs-v1estore.log
