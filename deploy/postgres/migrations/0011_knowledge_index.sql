-- Core metadata stores bounded real arrays; pgvector is required only when the search feature is enabled.
BEGIN;
SELECT pg_advisory_xact_lock(162109,1);
DO $$ BEGIN
    IF current_database()<>'assembleai_platform' THEN RAISE EXCEPTION 'Refusing platform migration in database %',current_database(); END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=11) THEN RETURN; END IF;
    IF NOT EXISTS(SELECT 1 FROM public.platform_schema_migrations WHERE version=10) THEN RAISE EXCEPTION 'Platform migration 10 is required before migration 11'; END IF;
    ALTER TABLE public.knowledge_documents DROP CONSTRAINT knowledge_documents_status_check;
    ALTER TABLE public.knowledge_documents ADD CONSTRAINT knowledge_documents_status_check CHECK(status IN ('queued','processing','extracted','indexing','ready','failed','deleted'));
    ALTER TABLE public.knowledge_documents DROP CONSTRAINT knowledge_documents_failure_code_check;
    ALTER TABLE public.knowledge_documents ADD CONSTRAINT knowledge_documents_failure_code_check CHECK(failure_code IS NULL OR failure_code IN
        ('ingestion_failed','ocr_required','encrypted_document','unsupported_document','empty_document','extracted_text_too_large','document_too_complex','invalid_document',
         'parser_timeout','parser_unavailable','embedding_unavailable','index_profile_mismatch','index_too_large'));
    ALTER TABLE public.knowledge_documents ADD COLUMN index_profile varchar(64) COLLATE "C" CHECK(index_profile IS NULL OR index_profile='e5-small-dd476dd0-384-v1');
    ALTER TABLE public.knowledge_documents ADD CONSTRAINT knowledge_documents_ready_profile CHECK(status<>'ready' OR index_profile='e5-small-dd476dd0-384-v1' AND index_profile IS NOT NULL);
    ALTER TABLE public.knowledge_jobs DROP CONSTRAINT knowledge_jobs_kind_check;
    ALTER TABLE public.knowledge_jobs ADD CONSTRAINT knowledge_jobs_kind_check CHECK(kind IN ('ingest','embed','purge'));
    ALTER TABLE public.knowledge_jobs ADD COLUMN next_chunk_index integer NOT NULL DEFAULT 0 CHECK(next_chunk_index BETWEEN 0 AND 4096);
    ALTER TABLE public.knowledge_chunks ADD CONSTRAINT knowledge_chunks_revision_key UNIQUE(tenant_id,document_id,document_revision,chunk_index);
    CREATE FUNCTION public.knowledge_embedding_valid(value real[]) RETURNS boolean
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog AS $function$
        SELECT array_ndims(value)=1 AND array_lower(value,1)=1 AND cardinality(value)=384
            AND NOT EXISTS(SELECT 1 FROM unnest(value) x WHERE x IS NULL OR NOT(x>'-Infinity'::real AND x<'Infinity'::real))
            AND (SELECT sum(x::double precision*x::double precision) FROM unnest(value) x) BETWEEN 0.998 AND 1.002
    $function$;
    CREATE TABLE public.knowledge_embeddings(
        tenant_id uuid NOT NULL,
        document_id uuid NOT NULL,
        document_revision bigint NOT NULL CHECK(document_revision>0),
        chunk_index integer NOT NULL CHECK(chunk_index BETWEEN 0 AND 4095),
        segment_index integer NOT NULL CHECK(segment_index BETWEEN 0 AND 63),
        profile varchar(64) COLLATE "C" NOT NULL CHECK(profile='e5-small-dd476dd0-384-v1'),
        content text NOT NULL CHECK(length(content) BETWEEN 1 AND 2400),
        locator varchar(200) NOT NULL CHECK(length(btrim(locator))>0),
        embedding real[] NOT NULL CHECK(public.knowledge_embedding_valid(embedding)),
        PRIMARY KEY(tenant_id,document_id,document_revision,chunk_index,segment_index),
        FOREIGN KEY(tenant_id,document_id,document_revision,chunk_index)
            REFERENCES public.knowledge_chunks(tenant_id,document_id,document_revision,chunk_index) ON DELETE CASCADE
    );
    CREATE TABLE public.platform_knowledge_searches(
        call_id uuid PRIMARY KEY,
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        request_id varchar(32) COLLATE "C" NOT NULL CHECK(request_id ~ '^[a-f0-9]{32}$'),
        authorization_version bigint NOT NULL CHECK(authorization_version>0),
        profile varchar(64) COLLATE "C" NOT NULL CHECK(profile='e5-small-dd476dd0-384-v1'),
        outcome varchar(16) COLLATE "C" NOT NULL CHECK(outcome IN ('running','completed','revoked','cancelled','timeout','failed')),
        hit_count integer CHECK(hit_count BETWEEN 0 AND 5),
        elapsed_milliseconds bigint CHECK(elapsed_milliseconds>=0),
        started_at timestamptz NOT NULL DEFAULT now(),finished_at timestamptz,
        app_id varchar(64) COLLATE "C",release_version varchar(64) COLLATE "C",run_id uuid,gateway_request_id varchar(32) COLLATE "C",
        CHECK((app_id IS NULL AND release_version IS NULL AND run_id IS NULL AND gateway_request_id IS NULL)
            OR (app_id IS NOT NULL AND app_id ~ '^[a-z][a-z0-9-]{0,63}$' AND release_version IS NOT NULL
                AND release_version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
                AND run_id IS NOT NULL AND run_id<>'00000000-0000-0000-0000-000000000000'
                AND gateway_request_id IS NOT NULL AND gateway_request_id ~ '^[a-f0-9]{32}$')),
        CHECK((outcome='running' AND finished_at IS NULL AND hit_count IS NULL AND elapsed_milliseconds IS NULL)
            OR (outcome<>'running' AND finished_at IS NOT NULL AND hit_count IS NOT NULL AND elapsed_milliseconds IS NOT NULL))
    );
    CREATE INDEX ix_knowledge_searches_recent ON public.platform_knowledge_searches(tenant_id,started_at DESC,call_id DESC);
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(11,'0011_knowledge_index.sql');
END $migration$;
COMMIT;
