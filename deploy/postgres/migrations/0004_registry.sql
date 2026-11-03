-- T4 global release management and independent tenant activation. No operators are seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=4) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=3) THEN
        RAISE EXCEPTION 'Platform migration 3 is required before migration 4';
    END IF;
    CREATE TABLE public.platform_registry_operators (
        user_id uuid NOT NULL REFERENCES public.platform_users(user_id) ON DELETE CASCADE,
        role varchar(32) COLLATE "C" NOT NULL CHECK (role IN ('developer','reviewer')),
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY(user_id,role)
    );
    CREATE TABLE public.platform_applications (
        app_id varchar(64) COLLATE "C" PRIMARY KEY CHECK (app_id ~ '^[a-z][a-z0-9-]{0,63}$'),
        name varchar(128) NOT NULL,
        category varchar(64) NOT NULL,
        owner_user_id uuid NOT NULL REFERENCES public.platform_users(user_id),
        created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE public.platform_application_releases (
        app_id varchar(64) COLLATE "C" NOT NULL REFERENCES public.platform_applications(app_id) ON DELETE CASCADE,
        release_version varchar(64) COLLATE "C" NOT NULL CHECK (release_version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'),
        manifest jsonb NOT NULL CHECK ((jsonb_typeof(manifest)='object' AND manifest->>'appId'=app_id AND manifest->>'version'=release_version AND manifest->>'status'='draft') IS TRUE),
        status varchar(32) COLLATE "C" NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','pending_review','published','disabled')),
        revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
        submitted_by uuid REFERENCES public.platform_users(user_id),
        published_at timestamptz,
        PRIMARY KEY(app_id,release_version),
        CHECK (status='draft' OR submitted_by IS NOT NULL),
        CHECK ((published_at IS NULL AND status IN ('draft','pending_review')) OR (published_at IS NOT NULL AND status IN ('published','disabled')))
    );
    CREATE TABLE public.tenant_applications (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        app_id varchar(64) COLLATE "C" NOT NULL,
        release_version varchar(64) COLLATE "C" NOT NULL,
        enabled boolean NOT NULL,
        revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
        PRIMARY KEY(tenant_id,app_id),
        FOREIGN KEY(app_id,release_version) REFERENCES public.platform_application_releases(app_id,release_version) ON DELETE CASCADE
    );
    -- No FK to mutable business rows: historical attribution survives their deletion.
    CREATE TABLE public.platform_registry_audit (
        event_id uuid PRIMARY KEY,
        actor_user_id uuid NOT NULL,
        tenant_id uuid,
        app_id varchar(64) COLLATE "C" NOT NULL,
        release_version varchar(64) COLLATE "C" NOT NULL,
        action varchar(32) COLLATE "C" NOT NULL,
        result varchar(32) COLLATE "C" NOT NULL,
        request_id varchar(128) COLLATE "C" NOT NULL,
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_registry_audit_app ON public.platform_registry_audit(app_id,occurred_at);
    CREATE FUNCTION public.guard_registry_release() RETURNS trigger LANGUAGE plpgsql AS $function$
    BEGIN
        IF NEW.app_id IS DISTINCT FROM OLD.app_id OR NEW.release_version IS DISTINCT FROM OLD.release_version
            OR NEW.revision <> OLD.revision+1 THEN RAISE EXCEPTION 'Invalid release revision or identity' USING ERRCODE='23514'; END IF;
        IF OLD.published_at IS NOT NULL AND (NEW.manifest IS DISTINCT FROM OLD.manifest
            OR NEW.published_at IS DISTINCT FROM OLD.published_at OR NEW.submitted_by IS DISTINCT FROM OLD.submitted_by) THEN
            RAISE EXCEPTION 'Published release content is immutable' USING ERRCODE='23514';
        END IF;
        IF NEW.manifest IS DISTINCT FROM OLD.manifest AND (OLD.status <> 'draft' OR NEW.status <> 'draft') THEN
            RAISE EXCEPTION 'Only drafts can be edited' USING ERRCODE='23514';
        END IF;
        IF NOT ((OLD.status='draft' AND NEW.status IN ('draft','pending_review'))
            OR (OLD.status='pending_review' AND NEW.status IN ('draft','published'))
            OR (OLD.status='published' AND NEW.status='disabled')
            OR (OLD.status='disabled' AND NEW.status='published')) THEN
            RAISE EXCEPTION 'Invalid release state transition' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END $function$;
    CREATE TRIGGER registry_release_guard BEFORE UPDATE ON public.platform_application_releases
        FOR EACH ROW EXECUTE FUNCTION public.guard_registry_release();
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(4,'0004_registry.sql');
END $migration$;
COMMIT;
