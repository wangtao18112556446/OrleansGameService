#!/bin/sh
set -eu

# psql 使用 libpq 环境变量，不能接收 Npgsql 格式的连接字符串。
# 官方脚本先完整下载，再统一执行，避免管道掩盖下载或 SQL 错误。
apk add --no-cache curl
schema_version=v10.2.1
mkdir -p /tmp/orleans-schema

# 部分网络无法访问 raw.githubusercontent.com。首选官方源，失败后使用 jsDelivr 镜像，
# 两者均固定到与项目 Orleans NuGet 包一致的发布标签，避免下载到不兼容的架构。
download_schema() {
    file_path=$1
    destination=$2
    primary_url="https://raw.githubusercontent.com/dotnet/orleans/$schema_version/$file_path"
    mirror_url="https://cdn.jsdelivr.net/gh/dotnet/orleans@$schema_version/$file_path"

    for url in "$primary_url" "$mirror_url"; do
        if curl --fail --location --retry 8 --connect-timeout 10 --max-time 120 \
            "$url" -o "$destination.part"; then
            mv "$destination.part" "$destination"
            return 0
        fi

        rm -f "$destination.part"
        echo "Failed to download $file_path from $url; trying the next source." >&2
    done

    echo "Unable to download $file_path from either configured source." >&2
    return 1
}

for file in PostgreSQL-Main.sql PostgreSQL-Clustering.sql PostgreSQL-Persistence.sql; do
    case "$file" in
        PostgreSQL-Main.sql) path="src/AdoNet/Shared/$file" ;;
        PostgreSQL-Clustering.sql) path="src/AdoNet/Orleans.Clustering.AdoNet/$file" ;;
        *) path="src/AdoNet/Orleans.Persistence.AdoNet/$file" ;;
    esac
    download_schema "$path" "/tmp/orleans-schema/$file"
done

# 表和基础查询均存在时接管旧安装；否则在同一事务内初始化，失败不留下半套架构。
# Orleans 10.2.1 的 ADO.NET 运行时要求 CleanupDefunctSiloEntriesKey，
# 但该版本随附的 PostgreSQL SQL 模板漏掉了该查询。这里补齐兼容迁移，
# 不重建 OrleansStorage，避免在升级或重启时丢失已持久化的 Grain 状态。
psql -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
SELECT pg_advisory_xact_lock(47613029);
SELECT to_regclass('public.orleansquery') IS NOT NULL
   AND to_regclass('public.orleansmembershiptable') IS NOT NULL
   AND to_regclass('public.orleansmembershipversiontable') IS NOT NULL
   AND to_regclass('public.orleansstorage') IS NOT NULL AS installed \gset
\if :installed
    SELECT COUNT(*) = 12 AS has_base_query FROM OrleansQuery WHERE QueryKey IN (
        'WriteToStorageKey', 'ReadFromStorageKey', 'ClearStorageKey', 'DeleteStorageKey',
        'UpdateIAmAlivetimeKey', 'InsertMembershipVersionKey', 'InsertMembershipKey', 'UpdateMembershipKey',
        'MembershipReadRowKey', 'MembershipReadAllKey', 'DeleteMembershipTableEntriesKey', 'GatewaysQueryKey'
    ) \gset
    \if :has_base_query
        \echo 'Applying Orleans 10.2.1 CleanupDefunctSiloEntries compatibility migration.'
    \else
        DO $$ BEGIN RAISE EXCEPTION 'Incomplete Orleans schema: missing one or more required base queries; inspect and repair before starting the gateway'; END $$;
    \endif
\else
    \i /tmp/orleans-schema/PostgreSQL-Main.sql
    \i /tmp/orleans-schema/PostgreSQL-Clustering.sql
    \i /tmp/orleans-schema/PostgreSQL-Persistence.sql
\endif

-- 6 是 Orleans.Runtime.SiloStatus.Dead。运行时仅传入 DeploymentId 和 IAmAliveTime；
-- 使用 UPSERT 让新库、旧库和重复执行都收敛到同一可用定义。
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES ('CleanupDefunctSiloEntriesKey', '
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND IAmAliveTime < @IAmAliveTime AND @IAmAliveTime IS NOT NULL
        AND Status = 6;
')
ON CONFLICT (QueryKey) DO UPDATE SET QueryText = EXCLUDED.QueryText;
COMMIT;
SQL

# 一次性初始化容器执行完毕后即退出（Exited (0) 属正常状态，并非启动失败）。
# 显式输出成功标记，便于在 compose 日志中确认初始化结果。
echo 'Orleans schema initialization completed successfully.'
