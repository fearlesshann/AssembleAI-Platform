-- T5 durable runtime leases and transport call records. No runtime data is seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=5) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=4) THEN
        RAISE EXCEPTION 'Platform migration 4 is required before migration 5';
    END IF;
    CREATE TABLE public.platform_gateway_runs (
        run_id uuid PRIMARY KEY,
        user_id uuid NOT NULL REFERENCES public.platform_users(user_id) ON DELETE CASCADE,
        tenant_id uuid NOT NULL,
        app_id varchar(64) COLLATE "C" NOT NULL,
        release_version varchar(64) COLLATE "C" NOT NULL,
        session_hash varchar(64) COLLATE "C" NOT NULL CHECK (session_hash ~ '^[a-f0-9]{64}$'),
        created_at timestamptz NOT NULL DEFAULT now(),
        expires_at timestamptz NOT NULL,
        drain_seconds integer NOT NULL CHECK (drain_seconds BETWEEN 1 AND 3600),
        drain_until timestamptz,
        revoked_at timestamptz,
        FOREIGN KEY(tenant_id,app_id) REFERENCES public.tenant_applications(tenant_id,app_id) ON DELETE CASCADE,
        FOREIGN KEY(app_id,release_version) REFERENCES public.platform_application_releases(app_id,release_version) ON DELETE CASCADE,
        CHECK (expires_at > created_at AND expires_at <= created_at+interval '1 day'),
        CHECK (drain_until IS NULL OR drain_until BETWEEN created_at AND expires_at)
    );
    CREATE INDEX ix_gateway_runs_user ON public.platform_gateway_runs(user_id,expires_at);
    CREATE INDEX ix_gateway_runs_release ON public.platform_gateway_runs(app_id,release_version,tenant_id) WHERE revoked_at IS NULL;
    -- Preserve attribution even after a lease, user, tenant or application is removed.
    CREATE TABLE public.platform_gateway_calls (
        request_id varchar(32) COLLATE "C" PRIMARY KEY CHECK (request_id ~ '^[a-f0-9]{32}$'),
        run_id uuid NOT NULL,
        user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        app_id varchar(64) COLLATE "C" NOT NULL,
        release_version varchar(64) COLLATE "C" NOT NULL,
        method varchar(16) COLLATE "C" NOT NULL CHECK (method ~ '^[A-Z]{1,16}$'),
        path varchar(2048) COLLATE "C" NOT NULL,
        started_at timestamptz NOT NULL DEFAULT now(),
        finished_at timestamptz,
        http_status integer CHECK (http_status=0 OR http_status BETWEEN 100 AND 599),
        outcome varchar(64) COLLATE "C" NOT NULL DEFAULT 'running' CHECK (outcome ~ '^[a-z][a-z0-9_]{0,63}$'),
        elapsed_milliseconds bigint CHECK (elapsed_milliseconds >= 0),
        CHECK ((outcome='running' AND finished_at IS NULL AND http_status IS NULL AND elapsed_milliseconds IS NULL)
            OR (outcome<>'running' AND finished_at IS NOT NULL AND http_status IS NOT NULL AND elapsed_milliseconds IS NOT NULL))
    );
    CREATE INDEX ix_gateway_calls_tenant_time ON public.platform_gateway_calls(tenant_id,started_at);
    CREATE INDEX ix_gateway_calls_unfinished ON public.platform_gateway_calls(started_at) WHERE finished_at IS NULL;
    CREATE FUNCTION public.drain_tenant_gateway_runs() RETURNS trigger LANGUAGE plpgsql AS $function$
    BEGIN
        IF NOT NEW.enabled THEN
            UPDATE public.platform_gateway_runs SET revoked_at=COALESCE(revoked_at,now())
            WHERE tenant_id=NEW.tenant_id AND app_id=NEW.app_id AND revoked_at IS NULL;
        ELSIF NEW.release_version IS DISTINCT FROM OLD.release_version THEN
            -- Never clear or extend a previous deadline when a pointer later rolls back.
            UPDATE public.platform_gateway_runs
            SET drain_until=LEAST(expires_at,now()+drain_seconds*interval '1 second')
            WHERE tenant_id=NEW.tenant_id AND app_id=NEW.app_id AND release_version=OLD.release_version
                AND revoked_at IS NULL AND drain_until IS NULL;
        END IF;
        RETURN NEW;
    END $function$;
    CREATE TRIGGER gateway_tenant_drain AFTER UPDATE ON public.tenant_applications
        FOR EACH ROW EXECUTE FUNCTION public.drain_tenant_gateway_runs();
    CREATE FUNCTION public.revoke_release_gateway_runs() RETURNS trigger LANGUAGE plpgsql AS $function$
    BEGIN
        IF NEW.status='disabled' THEN
            UPDATE public.platform_gateway_runs SET revoked_at=COALESCE(revoked_at,now())
            WHERE app_id=NEW.app_id AND release_version=NEW.release_version AND revoked_at IS NULL;
        END IF;
        RETURN NEW;
    END $function$;
    CREATE TRIGGER gateway_release_revoke AFTER UPDATE ON public.platform_application_releases
        FOR EACH ROW EXECUTE FUNCTION public.revoke_release_gateway_runs();
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(5,'0005_gateway.sql');
END $migration$;
COMMIT;
