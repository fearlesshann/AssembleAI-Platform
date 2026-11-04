-- T12 document metadata, durable ingestion intent and fenced text extraction. No content or accounts seeded.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=10) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=9) THEN
        RAISE EXCEPTION 'Platform migration 9 is required before migration 10';
    END IF;
    CREATE TABLE public.knowledge_documents (
        tenant_id uuid NOT NULL REFERENCES public.platform_tenants(tenant_id),
        document_id uuid NOT NULL CHECK (document_id <> '00000000-0000-0000-0000-000000000000'),
        file_name varchar(255) NOT NULL CHECK (length(btrim(file_name)) > 0),
        media_type varchar(128) COLLATE "C" NOT NULL,
        size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 5242880),
        sha256 varchar(64) COLLATE "C" NOT NULL CHECK (sha256 ~ '^[a-f0-9]{64}$'),
        organization_id uuid,
        status varchar(16) COLLATE "C" NOT NULL CHECK (status IN ('queued','processing','extracted','failed','deleted')),
        revision bigint NOT NULL CHECK (revision > 0),
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        failure_code varchar(32) COLLATE "C" CHECK (failure_code IS NULL OR failure_code IN
            ('ingestion_failed','ocr_required','encrypted_document','unsupported_document','empty_document',
             'extracted_text_too_large','document_too_complex','invalid_document','parser_timeout','parser_unavailable')),
        PRIMARY KEY(tenant_id,document_id),
        FOREIGN KEY(tenant_id,organization_id) REFERENCES public.tenant_organizations(tenant_id,organization_id)
    );
    CREATE INDEX ix_knowledge_documents_recent ON public.knowledge_documents(tenant_id,created_at DESC,document_id DESC);
    CREATE TABLE public.knowledge_jobs (
        job_id uuid PRIMARY KEY,
        tenant_id uuid NOT NULL,
        document_id uuid NOT NULL,
        kind varchar(8) COLLATE "C" NOT NULL CHECK (kind IN ('ingest','purge')),
        document_revision bigint NOT NULL CHECK (document_revision > 0),
        status varchar(16) COLLATE "C" NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','completed','failed','cancelled')),
        attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 3),
        lease_id uuid,
        lease_until timestamptz,
        available_at timestamptz NOT NULL DEFAULT now(),
        created_at timestamptz NOT NULL DEFAULT now(),
        FOREIGN KEY(tenant_id,document_id) REFERENCES public.knowledge_documents(tenant_id,document_id),
        UNIQUE(tenant_id,document_id,kind,document_revision),
        CHECK ((status='running' AND lease_id IS NOT NULL AND lease_until IS NOT NULL)
            OR (status<>'running' AND lease_id IS NULL AND lease_until IS NULL))
    );
    CREATE INDEX ix_knowledge_jobs_pending ON public.knowledge_jobs(available_at,created_at) WHERE status IN ('queued','running');
    CREATE TABLE public.knowledge_chunks (
        tenant_id uuid NOT NULL,
        document_id uuid NOT NULL,
        document_revision bigint NOT NULL CHECK (document_revision > 0),
        chunk_index integer NOT NULL CHECK (chunk_index BETWEEN 0 AND 4095),
        locator varchar(256) NOT NULL,
        content text NOT NULL CHECK (length(content) BETWEEN 1 AND 8192),
        PRIMARY KEY(tenant_id,document_id,chunk_index),
        FOREIGN KEY(tenant_id,document_id) REFERENCES public.knowledge_documents(tenant_id,document_id)
    );
    CREATE TABLE public.platform_knowledge_audit (
        event_id uuid PRIMARY KEY,
        tenant_id uuid NOT NULL,
        document_id uuid NOT NULL,
        actor_user_id uuid NOT NULL,
        action varchar(16) COLLATE "C" NOT NULL CHECK (action IN ('create','delete')),
        revision bigint NOT NULL,
        request_id varchar(32) COLLATE "C" NOT NULL CHECK (request_id ~ '^[a-f0-9]{32}$'),
        occurred_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_knowledge_audit_document ON public.platform_knowledge_audit(tenant_id,document_id,occurred_at);
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(10,'0010_knowledge.sql');
END $migration$;
COMMIT;
