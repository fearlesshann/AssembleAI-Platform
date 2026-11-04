-- T10 tenant connector policies and metadata-only execution audit; no data sources or grants are seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=8) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=7) THEN
        RAISE EXCEPTION 'Platform migration 7 is required before migration 8';
    END IF;
    CREATE TABLE public.tenant_connectors (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        source_id varchar(64) COLLATE "C" NOT NULL CHECK (source_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        enabled boolean NOT NULL,
        max_rows integer NOT NULL CHECK (max_rows BETWEEN 1 AND 500),
        timeout_seconds integer NOT NULL CHECK (timeout_seconds BETWEEN 1 AND 30),
        revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
        PRIMARY KEY (tenant_id,source_id)
    );
    -- Audit survives deletion of mutable configuration and identity records.
    CREATE TABLE public.platform_connector_configuration_audit (
        event_id uuid PRIMARY KEY,
        actor_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        source_id varchar(64) COLLATE "C" NOT NULL CHECK (source_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        request_id varchar(32) COLLATE "C" NOT NULL CHECK (request_id ~ '^[a-f0-9]{32}$'),
        revision bigint NOT NULL CHECK (revision > 0),
        enabled boolean NOT NULL,
        max_rows integer NOT NULL CHECK (max_rows BETWEEN 1 AND 500),
        timeout_seconds integer NOT NULL CHECK (timeout_seconds BETWEEN 1 AND 30),
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_connector_configuration_audit_tenant_source_time
        ON public.platform_connector_configuration_audit(tenant_id,source_id,occurred_at);
    CREATE TABLE public.platform_connector_calls (
        call_id uuid PRIMARY KEY,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        source_id varchar(64) COLLATE "C" NOT NULL CHECK (source_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        dataset_id varchar(64) COLLATE "C",
        operation varchar(8) COLLATE "C" NOT NULL CHECK (operation IN ('query','test')),
        request_id varchar(32) COLLATE "C" NOT NULL UNIQUE CHECK (request_id ~ '^[a-f0-9]{32}$'),
        started_at timestamptz NOT NULL DEFAULT now(),
        finished_at timestamptz,
        outcome varchar(32) COLLATE "C" NOT NULL DEFAULT 'running'
            CHECK (outcome IN ('running','completed','cancelled','revoked','timeout','upstream_error','failed')),
        row_count integer,
        elapsed_milliseconds bigint CHECK (elapsed_milliseconds >= 0),
        CHECK (((operation='query' AND dataset_id IS NOT NULL AND dataset_id ~ '^[a-z][a-z0-9._-]{0,63}$')
            OR (operation='test' AND dataset_id IS NULL)) IS TRUE),
        CHECK (((operation='query' AND outcome='completed' AND row_count IS NOT NULL AND row_count >= 0)
            OR ((operation<>'query' OR outcome<>'completed') AND row_count IS NULL)) IS TRUE),
        CHECK ((outcome='running' AND finished_at IS NULL AND elapsed_milliseconds IS NULL)
            OR (outcome<>'running' AND finished_at IS NOT NULL AND elapsed_milliseconds IS NOT NULL))
    );
    CREATE INDEX ix_connector_calls_tenant_source_time
        ON public.platform_connector_calls(tenant_id,source_id,started_at DESC,call_id DESC);
    CREATE INDEX ix_connector_calls_unfinished ON public.platform_connector_calls(started_at) WHERE finished_at IS NULL;
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(8,'0008_connectors.sql');
END $migration$;
COMMIT;
