@echo off
rem
rem Updates the phone's app in place, keeping the account, the chats and the logs.
rem Double click it after a new WhatsApp-<versione>-ARM-Release.xap lands on the
rem desktop: it picks the newest one and runs tools\phone-update.ps1 with it.
rem
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Vincenzo\Documents\WhatsAppForWP\tools\phone-update.ps1" %*
echo.
pause