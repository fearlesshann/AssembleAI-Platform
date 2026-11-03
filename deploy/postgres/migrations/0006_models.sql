-- T9 tenant model policy and metadata-only call attribution. No models or grants are seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=6) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=5) THEN
        RAISE EXCEPTION 'Platform migration 5 is required before migration 6';
    END IF;
    CREATE TABLE public.tenant_models (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        model_id varchar(64) COLLATE "C" NOT NULL CHECK (model_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        enabled boolean NOT NULL,
        max_output_tokens integer NOT NULL CHECK (max_output_tokens > 0),
        revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
        PRIMARY KEY(tenant_id,model_id)
    );
    -- Historical attribution is independent of deletable tenant/user/configuration rows.
    CREATE TABLE public.platform_model_configuration_audit (
        event_id uuid PRIMARY KEY,
        actor_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        model_id varchar(64) COLLATE "C" NOT NULL CHECK (model_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        request_id varchar(32) COLLATE "C" NOT NULL CHECK (request_id ~ '^[a-f0-9]{32}$'),
        revision bigint NOT NULL CHECK (revision > 0),
        enabled boolean NOT NULL,
        max_output_tokens integer NOT NULL CHECK (max_output_tokens > 0),
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_model_configuration_audit_tenant_model_time
        ON public.platform_model_configuration_audit(tenant_id,model_id,occurred_at);
    CREATE TABLE public.platform_model_calls (
        call_id uuid PRIMARY KEY,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        model_id varchar(64) COLLATE "C" NOT NULL CHECK (model_id ~ '^[a-z][a-z0-9._-]{0,63}$'),
        request_id varchar(32) COLLATE "C" NOT NULL UNIQUE CHECK (request_id ~ '^[a-f0-9]{32}$'),
        started_at timestamptz NOT NULL DEFAULT now(),
        finished_at timestamptz,
        outcome varchar(32) COLLATE "C" NOT NULL DEFAULT 'running'
            CHECK (outcome IN ('running','completed','cancelled','revoked','timeout','upstream_error','failed')),
        elapsed_milliseconds bigint CHECK (elapsed_milliseconds >= 0),
        prompt_tokens bigint,
        completion_tokens bigint,
        total_tokens bigint,
        CHECK ((prompt_tokens IS NULL AND completion_tokens IS NULL AND total_tokens IS NULL)
            OR (prompt_tokens IS NOT NULL AND completion_tokens IS NOT NULL AND total_tokens IS NOT NULL
                AND prompt_tokens >= 0 AND completion_tokens >= 0 AND total_tokens >= 0)),
        CHECK ((outcome='running' AND finished_at IS NULL AND elapsed_milliseconds IS NULL AND prompt_tokens IS NULL)
            OR (outcome<>'running' AND finished_at IS NOT NULL AND elapsed_milliseconds IS NOT NULL))
    );
    CREATE INDEX ix_model_calls_tenant_model_time ON public.platform_model_calls(tenant_id,model_id,started_at DESC,call_id DESC);
    CREATE INDEX ix_model_calls_unfinished ON public.platform_model_calls(started_at) WHERE finished_at IS NULL;
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(6,'0006_models.sql');
END $migration$;
COMMIT;
