# Запуск на своєму комп’ютері

Для перевірки викладачу достатньо README та збережених результатів. Команди нижче потрібні для повторення досліду.

## Windows

Потрібні .NET SDK 10 і Docker Compose. На комп’ютері, де зроблено заміри, Docker працює в Ubuntu 24.04 через WSL2, а для запуску зібраної програми в Ubuntu встановлено `dotnet-runtime-10.0`.

```powershell
./scripts/lab.ps1 -Mode start
./scripts/lab.ps1 -Mode show
```

`show` читає вже завантажені дані. У порожньому середовищі спочатку виконати `verify` або `benchmark`.

```powershell
./scripts/lab.ps1 -Mode verify -Output results/verify
./scripts/lab.ps1 -Mode quick -Output results/quick
./scripts/lab.ps1 -Mode benchmark -Output results/benchmark
```

Режими `verify`, `quick` і `benchmark` створюють дані заново лише у виділеній базі `postsystem_nosql`. У PostgreSQL замінюється схема `tracking`, у MongoDB колекція `shipments`. Після `benchmark` залишається набір на 100 000 відправлень. `quick` потрібен для швидкої перевірки запуску і не замінює повного заміру.

Приклад Redis на цьому комп’ютері запускається окремо.

```powershell
wsl -d Ubuntu-24.04 -u root -- sh redis/demo.sh
```

`./scripts/lab.ps1 -Mode stop` зупиняє контейнери цієї роботи і зберігає їхні дані.

## Linux або Docker Desktop

Команди Bash із кореня репозиторію.

```bash
docker compose up -d --wait
export POSTAL_PG='Host=127.0.0.1;Port=55432;Database=postsystem_nosql;Username=postal;Password=postal_lab'
export POSTAL_MONGO='mongodb://postal:postal_lab@127.0.0.1:57017/?authSource=admin&directConnection=true'
export POSTAL_MONGO_DB=postsystem_nosql
dotnet restore src/PostalTracking.csproj --locked-mode
dotnet build src/PostalTracking.csproj -c Release --no-restore
dotnet run --project src/PostalTracking.csproj -c Release --no-build -- verify results/verify
dotnet run --project src/PostalTracking.csproj -c Release --no-build -- benchmark results/benchmark
dotnet run --project src/PostalTracking.csproj -c Release --no-build -- show results/show
sh redis/demo.sh
```

Порти у Compose доступні лише локально. Значення `postal_lab` належить тестовим контейнерам. З’єднання з базою попередньої роботи не потрібне.

Автоматична перевірка використовує Ubuntu 22.04 з ядром 6.8 для запуску тієї самої версії MongoDB. Для повторення досліду на іншому комп’ютері варто перевірити [сумісність ядра Linux](https://www.mongodb.com/docs/manual/release-notes/8.0/).
