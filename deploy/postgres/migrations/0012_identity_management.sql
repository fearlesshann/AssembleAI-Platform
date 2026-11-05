-- Explicit identity administration, independent from registry operators and business grants.
BEGIN;
SELECT pg_advisory_xact_lock(162109,1);
DO $$ BEGIN
    IF current_database()<>'assembleai_platform' THEN RAISE EXCEPTION 'Refusing platform migration in database %',current_database(); END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=12) THEN RETURN; END IF;
    IF NOT EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=11) THEN RAISE EXCEPTION 'Platform migration 11 is required before migration 12'; END IF;
    ALTER TABLE public.platform_users ADD COLUMN identity_revision bigint NOT NULL DEFAULT 1 CHECK(identity_revision>0);
    ALTER TABLE public.platform_tenants ADD COLUMN identity_revision bigint NOT NULL DEFAULT 1 CHECK(identity_revision>0);
    ALTER TABLE public.tenant_memberships ADD COLUMN identity_revision bigint NOT NULL DEFAULT 1 CHECK(identity_revision>0);
    ALTER TABLE public.tenant_organizations ADD COLUMN identity_revision bigint NOT NULL DEFAULT 1 CHECK(identity_revision>0);
    ALTER TABLE public.tenant_roles ADD COLUMN identity_revision bigint NOT NULL DEFAULT 1 CHECK(identity_revision>0);
    CREATE TABLE public.platform_identity_administrators(
        user_id uuid PRIMARY KEY REFERENCES public.platform_users(user_id) ON DELETE CASCADE,
        enabled boolean NOT NULL DEFAULT true
    );
    CREATE TABLE public.tenant_identity_administrators(
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY(tenant_id,user_id),
        FOREIGN KEY(tenant_id,user_id) REFERENCES public.tenant_memberships(tenant_id,user_id) ON DELETE CASCADE
    );
    CREATE TABLE public.platform_identity_audit(
        event_id uuid PRIMARY KEY,
        actor_user_id uuid NOT NULL,
        tenant_id uuid,
        entity_type varchar(32) NOT NULL,
        entity_id uuid NOT NULL,
        action varchar(32) NOT NULL,
        result varchar(32) NOT NULL,
        request_id varchar(128) NOT NULL,
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_identity_audit_tenant ON public.platform_identity_audit(tenant_id,occurred_at DESC,event_id DESC);
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(12,'0012_identity_management.sql');
END $migration$;
COMMIT;
