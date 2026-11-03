-- Core platform bootstrap. Knowledge-base extension setup is enable-vector.sql.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$
BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;

SET LOCAL ROLE assembleai_platform;
CREATE TABLE IF NOT EXISTS public.platform_schema_migrations (
    version integer PRIMARY KEY,
    filename text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO public.platform_schema_migrations(version, filename)
VALUES (1, '0001_bootstrap.sql')
ON CONFLICT (version) DO NOTHING;
COMMIT;
