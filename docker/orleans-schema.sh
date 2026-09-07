#!/bin/sh
set -eu

# psql 使用 libpq 环境变量，不能接收 Npgsql 格式的连接字符串。
# 官方脚本先完整下载，再统一执行，避免管道掩盖下载或 SQL 错误。
apk add --no-cache curl
schema_version=v10.2.1
mkdir -p /tmp/orleans-schema
for file in PostgreSQL-Main.sql PostgreSQL-Clustering.sql PostgreSQL-Persistence.sql; do
    case "$file" in
        PostgreSQL-Main.sql) path="src/AdoNet/Shared/$file" ;;
        PostgreSQL-Clustering.sql) path="src/AdoNet/Orleans.Clustering.AdoNet/$file" ;;
        *) path="src/AdoNet/Orleans.Persistence.AdoNet/$file" ;;
    esac
    curl --fail --location --retry 8 "https://raw.githubusercontent.com/dotnet/orleans/$schema_version/$path" -o "/tmp/orleans-schema/$file"
done

# 表和查询均存在时接管旧安装；否则在同一事务内初始化，失败不留下半套架构。
psql -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
SELECT pg_advisory_xact_lock(47613029);
SELECT to_regclass('public.orleansquery') IS NOT NULL
   AND to_regclass('public.orleansmembershiptable') IS NOT NULL
   AND to_regclass('public.orleansmembershipversiontable') IS NOT NULL
   AND to_regclass('public.orleansstorage') IS NOT NULL AS installed \gset
\if :installed
    SELECT COUNT(*) = 12 AS has_storage_query FROM OrleansQuery WHERE QueryKey IN (
        'WriteToStorageKey', 'ReadFromStorageKey', 'ClearStorageKey', 'DeleteStorageKey',
        'UpdateIAmAlivetimeKey', 'InsertMembershipVersionKey', 'InsertMembershipKey', 'UpdateMembershipKey',
        'MembershipReadRowKey', 'MembershipReadAllKey', 'DeleteMembershipTableEntriesKey', 'GatewaysQueryKey'
    ) \gset
    \if :has_storage_query
        \echo 'Orleans schema already installed.'
    \else
        DO $$ BEGIN RAISE EXCEPTION 'Incomplete Orleans schema: inspect and repair before starting the gateway'; END $$;
    \endif
\else
    \i /tmp/orleans-schema/PostgreSQL-Main.sql
    \i /tmp/orleans-schema/PostgreSQL-Clustering.sql
    \i /tmp/orleans-schema/PostgreSQL-Persistence.sql
\endif
COMMIT;
SQL
