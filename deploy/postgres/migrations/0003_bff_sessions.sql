-- Server-side BFF tickets only. No accounts or sessions are seeded.
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
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version = 3) THEN
        RETURN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version = 2) THEN
        RAISE EXCEPTION 'Platform migration 2 is required before migration 3';
    END IF;

    CREATE TABLE public.platform_bff_sessions (
        session_key varchar(43) COLLATE "C" PRIMARY KEY
            CHECK (session_key ~ '^[A-Za-z0-9_-]{43}$'),
        -- Application DataProtection encrypts and authenticates the complete ticket.
        -- The key ring and its encryption certificate are held outside PostgreSQL.
        protected_ticket bytea NOT NULL CHECK (octet_length(protected_ticket) > 0),
        expires_at timestamptz NOT NULL
    );
    CREATE INDEX ix_platform_bff_sessions_expiry ON public.platform_bff_sessions(expires_at);

    INSERT INTO public.platform_schema_migrations(version, filename)
        VALUES (3, '0003_bff_sessions.sql');
END
$migration$;
COMMIT;
