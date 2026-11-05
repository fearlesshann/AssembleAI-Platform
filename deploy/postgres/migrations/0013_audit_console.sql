-- Safe history querying and attributable bounded exports; no retention deletion or seeded rights.
BEGIN;
SELECT pg_advisory_xact_lock(162109,1);
DO $$ BEGIN
    IF current_database()<>'assembleai_platform' THEN RAISE EXCEPTION 'Refusing platform migration in database %',current_database(); END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=13) THEN RETURN; END IF;
    IF NOT EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=12) THEN RAISE EXCEPTION 'Platform migration 12 is required before migration 13'; END IF;
    CREATE TABLE public.platform_audit_exports(
        event_id uuid PRIMARY KEY,tenant_id uuid NOT NULL,actor_user_id uuid NOT NULL,
        request_id varchar(32) COLLATE "C" NOT NULL CHECK(request_id ~ '^[a-f0-9]{32}$'),
        record_count integer NOT NULL CHECK(record_count BETWEEN 0 AND 1000),truncated boolean NOT NULL,
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_audit_exports_time ON public.platform_audit_exports(tenant_id,occurred_at DESC,event_id DESC);
    CREATE INDEX ix_audit_exports_request ON public.platform_audit_exports(tenant_id,request_id);
    CREATE INDEX ix_audit_registry_time ON public.platform_registry_audit(tenant_id,occurred_at DESC,event_id DESC);
    CREATE INDEX ix_audit_registry_request ON public.platform_registry_audit(tenant_id,request_id);
    CREATE INDEX ix_audit_identity_request ON public.platform_identity_audit(tenant_id,request_id);
    CREATE INDEX ix_audit_model_time ON public.platform_model_calls(tenant_id,started_at DESC,call_id DESC);
    CREATE INDEX ix_audit_model_gateway ON public.platform_model_calls(tenant_id,gateway_request_id);
    CREATE INDEX ix_audit_model_config_time ON public.platform_model_configuration_audit(tenant_id,occurred_at DESC,event_id DESC);
    CREATE INDEX ix_audit_model_config_request ON public.platform_model_configuration_audit(tenant_id,request_id);
    CREATE INDEX ix_audit_connector_time ON public.platform_connector_calls(tenant_id,started_at DESC,call_id DESC);
    CREATE INDEX ix_audit_connector_gateway ON public.platform_connector_calls(tenant_id,gateway_request_id);
    CREATE INDEX ix_audit_connector_config_time ON public.platform_connector_configuration_audit(tenant_id,occurred_at DESC,event_id DESC);
    CREATE INDEX ix_audit_connector_config_request ON public.platform_connector_configuration_audit(tenant_id,request_id);
    CREATE INDEX ix_audit_knowledge_time ON public.platform_knowledge_audit(tenant_id,occurred_at DESC,event_id DESC);
    CREATE INDEX ix_audit_knowledge_request ON public.platform_knowledge_audit(tenant_id,request_id);
    CREATE INDEX ix_audit_search_request ON public.platform_knowledge_searches(tenant_id,request_id);
    CREATE INDEX ix_audit_search_gateway ON public.platform_knowledge_searches(tenant_id,gateway_request_id);
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(13,'0013_audit_console.sql');
END $migration$;
COMMIT;
