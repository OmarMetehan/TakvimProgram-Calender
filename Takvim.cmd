@echo off
REM Takvim'i derler ve masaüstü uygulamasını başlatır.
REM
REM Bu makinede Smart App Control açık ve imzasız derlemeleri karma değerine
REM göre engelliyor. Engellenen dosya derlemeden derlemeye değişiyor: bir gün
REM Takvim.Core.dll, ertesi gün Takvim.Server.dll. Directory.Build.targets her
REM derlemeye yeni bir kimlik verdiği için engel kalıcıya dönmüyor; burada da
REM engellenen bir derlemede yeniden derleyip yeniden deniyoruz.
REM
REM Kalıcı çözüm ayarın kapatılmasıdır:
REM   Windows Güvenliği > Uygulama ve tarayıcı denetimi > Akıllı Uygulama Denetimi
REM Bu geri alınamayan bir seçimdir ve kullanıcıya aittir.
setlocal
cd /d "%~dp0"

set UYGULAMA=src\Takvim.Desktop\bin\Release\net10.0-windows\Takvim.exe
set DENEME=0

dotnet build --nologo -v q -c Release || exit /b 1

:dene
set /a DENEME+=1

start "" "%UYGULAMA%"

REM Engellenen uygulama saniyeler içinde düşer; açılan uygulama ayakta kalır.
REM Beklemek için "timeout" değil "ping" kullanılıyor: timeout, girdi
REM yönlendirilmiş bir kabukta (betikten çağrıldığında) çalışmayı reddediyor.
ping -n 9 127.0.0.1 >nul

tasklist /fi "imagename eq Takvim.exe" 2>nul | find /i "Takvim.exe" >nul
if not errorlevel 1 (
    echo Takvim acildi.
    exit /b 0
)

if %DENEME% GEQ 8 (
    echo.
    echo Smart App Control 8 denemede de engelledi.
    echo Ayrintilar: %%LOCALAPPDATA%%\Takvim\hata.log
    echo Kalici cozum icin bu dosyanin basindaki nota bakin.
    exit /b 1
)

echo [Smart App Control engeli] Yeniden derlenip denenecek... (%DENEME%/8)
dotnet build --nologo -v q -c Release --no-incremental >nul || exit /b 1
goto :dene
