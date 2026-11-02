-- T3 identity and authorization foundation. No accounts, memberships or grants are seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $database$
BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END
$database$;
SET LOCAL ROLE assembleai_platform;

DO $migration$
BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version = 2) THEN
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version = 1) THEN
        RAISE EXCEPTION 'Platform migration 1 is required before migration 2';
    END IF;

    CREATE TABLE public.platform_users (
        user_id uuid PRIMARY KEY CHECK (user_id <> '00000000-0000-0000-0000-000000000000'),
        issuer varchar(2048) COLLATE "C" NOT NULL CHECK (length(btrim(issuer)) > 0),
        subject varchar(512) COLLATE "C" NOT NULL CHECK (length(btrim(subject)) > 0),
        display_name varchar(256) NOT NULL CHECK (length(btrim(display_name)) > 0),
        enabled boolean NOT NULL DEFAULT true,
        created_at timestamptz NOT NULL DEFAULT now(),
        UNIQUE (issuer, subject)
    );

    CREATE TABLE public.platform_tenants (
        tenant_id uuid PRIMARY KEY CHECK (tenant_id <> '00000000-0000-0000-0000-000000000000'),
        name varchar(256) NOT NULL CHECK (length(btrim(name)) > 0),
        enabled boolean NOT NULL DEFAULT true,
        authorization_version bigint NOT NULL DEFAULT 1 CHECK (authorization_version > 0),
        created_at timestamptz NOT NULL DEFAULT now()
    );

    CREATE TABLE public.tenant_memberships (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        user_id uuid NOT NULL REFERENCES public.platform_users(user_id) ON DELETE CASCADE,
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY (tenant_id, user_id)
    );
    CREATE INDEX ix_tenant_memberships_user ON public.tenant_memberships(user_id, tenant_id);

    -- Flat explicit scopes in this foundation. No implicit ancestor/descendant expansion.
    CREATE TABLE public.tenant_organizations (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        organization_id uuid NOT NULL CHECK (organization_id <> '00000000-0000-0000-0000-000000000000'),
        name varchar(256) NOT NULL CHECK (length(btrim(name)) > 0),
        kind varchar(64) COLLATE "C" NOT NULL CHECK (kind ~ '^[a-z][a-z0-9._-]{0,63}$'),
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY (tenant_id, organization_id)
    );

    CREATE TABLE public.tenant_roles (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id) ON DELETE CASCADE,
        role_id uuid NOT NULL CHECK (role_id <> '00000000-0000-0000-0000-000000000000'),
        name varchar(128) NOT NULL CHECK (length(btrim(name)) > 0),
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY (tenant_id, role_id),
        UNIQUE (tenant_id, name)
    );

    CREATE TABLE public.tenant_member_roles (
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        role_id uuid NOT NULL,
        PRIMARY KEY (tenant_id, user_id, role_id),
        FOREIGN KEY (tenant_id, user_id)
            REFERENCES public.tenant_memberships(tenant_id, user_id) ON DELETE CASCADE,
        FOREIGN KEY (tenant_id, role_id)
            REFERENCES public.tenant_roles(tenant_id, role_id) ON DELETE CASCADE
    );
    CREATE INDEX ix_tenant_member_roles_role ON public.tenant_member_roles(tenant_id, role_id);

    -- Each grant binds a permission, one concrete resource and its organization scope.
    -- A tenant-wide grant can authorize a tenant-level resource (organization_id is absent).
    CREATE TABLE public.role_permission_grants (
        tenant_id uuid NOT NULL,
        grant_id uuid NOT NULL CHECK (grant_id <> '00000000-0000-0000-0000-000000000000'),
        role_id uuid NOT NULL,
        permission varchar(128) COLLATE "C" NOT NULL CHECK (permission ~ '^[a-z][a-z0-9:._-]{0,127}$'),
        resource_type varchar(64) COLLATE "C" NOT NULL CHECK (resource_type ~ '^[a-z][a-z0-9._-]{0,63}$'),
        resource_id varchar(256) COLLATE "C" NOT NULL
            CHECK (length(resource_id) > 0 AND resource_id = btrim(resource_id) AND position('*' in resource_id) = 0),
        tenant_wide boolean NOT NULL DEFAULT false,
        enabled boolean NOT NULL DEFAULT true,
        PRIMARY KEY (tenant_id, grant_id),
        FOREIGN KEY (tenant_id, role_id)
            REFERENCES public.tenant_roles(tenant_id, role_id) ON DELETE CASCADE
    );
    CREATE INDEX ix_role_permission_grants_role ON public.role_permission_grants(tenant_id, role_id);

    CREATE TABLE public.grant_organizations (
        tenant_id uuid NOT NULL,
        grant_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        PRIMARY KEY (tenant_id, grant_id, organization_id),
        FOREIGN KEY (tenant_id, grant_id)
            REFERENCES public.role_permission_grants(tenant_id, grant_id) ON DELETE CASCADE,
        FOREIGN KEY (tenant_id, organization_id)
            REFERENCES public.tenant_organizations(tenant_id, organization_id) ON DELETE CASCADE
    );
    CREATE INDEX ix_grant_organizations_organization ON public.grant_organizations(tenant_id, organization_id);

    -- An epoch supports diagnostics/future invalidation, not a cached authorization grant.
    -- Changing authorization rows bumps the epoch atomically in the writer's transaction.
    CREATE FUNCTION public.bump_tenant_authorization_version() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            UPDATE public.platform_tenants SET authorization_version = authorization_version + 1
                WHERE tenant_id = OLD.tenant_id;
        ELSE
            IF TG_OP = 'UPDATE' THEN
                IF OLD.tenant_id IS DISTINCT FROM NEW.tenant_id THEN
                    UPDATE public.platform_tenants SET authorization_version = authorization_version + 1
                        WHERE tenant_id = OLD.tenant_id;
                END IF;
            END IF;
            UPDATE public.platform_tenants SET authorization_version = authorization_version + 1
                WHERE tenant_id = NEW.tenant_id;
        END IF;
        RETURN NULL;
    END
    $function$;

    CREATE TRIGGER memberships_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.tenant_memberships FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();
    CREATE TRIGGER organizations_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.tenant_organizations FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();
    CREATE TRIGGER roles_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.tenant_roles FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();
    CREATE TRIGGER member_roles_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.tenant_member_roles FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();
    CREATE TRIGGER grants_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.role_permission_grants FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();
    CREATE TRIGGER grant_organizations_authorization_version AFTER INSERT OR UPDATE OR DELETE
        ON public.grant_organizations FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_authorization_version();

    CREATE FUNCTION public.bump_user_tenant_authorization_versions() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
    BEGIN
        IF NEW.enabled IS DISTINCT FROM OLD.enabled OR NEW.issuer IS DISTINCT FROM OLD.issuer
            OR NEW.subject IS DISTINCT FROM OLD.subject THEN
            UPDATE public.platform_tenants t SET authorization_version = authorization_version + 1
                WHERE EXISTS (SELECT 1 FROM public.tenant_memberships m
                    WHERE m.tenant_id = t.tenant_id AND m.user_id = NEW.user_id);
        END IF;
        RETURN NULL;
    END
    $function$;
    CREATE TRIGGER users_authorization_version AFTER UPDATE OF enabled, issuer, subject
        ON public.platform_users FOR EACH ROW EXECUTE FUNCTION public.bump_user_tenant_authorization_versions();

    CREATE FUNCTION public.bump_tenant_state_authorization_version() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
    BEGIN
        IF NEW.enabled IS DISTINCT FROM OLD.enabled THEN
            NEW.authorization_version := OLD.authorization_version + 1;
        END IF;
        RETURN NEW;
    END
    $function$;
    CREATE TRIGGER tenants_authorization_version BEFORE UPDATE OF enabled
        ON public.platform_tenants FOR EACH ROW EXECUTE FUNCTION public.bump_tenant_state_authorization_version();

    INSERT INTO public.platform_schema_migrations(version, filename)
        VALUES (2, '0002_authorization.sql');
END
$migration$;
COMMIT;
