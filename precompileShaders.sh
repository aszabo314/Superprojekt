#!/bin/sh

mkdir tmp
cd tmp
#
npm init -y
npm install puppeteer

dotnet run --project ../src/Superserver/Superserver.fsproj --urls "http://localhost:5432" 2>&1 | tee ./myserver.log &
SERVER_PID=$!
echo "started server with pid $SERVER_PID"

until grep -q "Now listening on: http://localhost:5432" ./myserver.log; do
  sleep 1.0
  echo "waiting for server to start..."
done

echo "server running"

cp ../src/Superserver/precompile.js ./index.js
node index.js > ../src/Superprojekt/ShaderCache.fs

kill -9 $(lsof -t -i :5432)
 
cd ..
rm -dfr tmp