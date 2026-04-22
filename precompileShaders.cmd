@echo off
setlocal

if exist tmp rmdir /s /q tmp
mkdir tmp
cd tmp

call npm init -y
call npm install puppeteer

powershell -NoProfile -Command "(Start-Process -FilePath dotnet -ArgumentList 'run','--project','..\src\Superserver\Superserver.fsproj','--urls','http://localhost:5432' -RedirectStandardOutput myserver.log -RedirectStandardError myserver.err -PassThru).Id" > server.pid
set /p SERVER_PID=<server.pid
del server.pid
echo started server with pid %SERVER_PID%

:waitloop
findstr /c:"Now listening on: http://localhost:5432" myserver.log >nul 2>&1
if errorlevel 1 (
    timeout /t 1 /nobreak >nul
    echo waiting for server to start...
    goto waitloop
)

echo server running

copy ..\src\Superserver\precompile.js index.js >nul
node index.js > ..\src\Superprojekt\ShaderCache.fs

taskkill /PID %SERVER_PID% /T /F >nul 2>&1

cd ..

:cleanup
rmdir /s /q tmp 2>nul
if exist tmp (
    timeout /t 1 /nobreak >nul
    goto cleanup
)

endlocal
