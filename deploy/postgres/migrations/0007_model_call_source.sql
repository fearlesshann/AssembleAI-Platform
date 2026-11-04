-- Model calls retain the trusted application origin without retaining credentials or content.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$ BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;
SET LOCAL ROLE assembleai_platform;
DO $migration$ BEGIN
    IF EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=7) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version=6) THEN
        RAISE EXCEPTION 'Platform migration 6 is required before migration 7';
    END IF;
    ALTER TABLE public.platform_model_calls
        ADD COLUMN app_id varchar(64) COLLATE "C",
        ADD COLUMN release_version varchar(64) COLLATE "C",
        ADD COLUMN run_id uuid,
        ADD COLUMN gateway_request_id varchar(32) COLLATE "C",
        ADD CONSTRAINT model_call_source_complete CHECK (
            ((app_id IS NULL AND release_version IS NULL AND run_id IS NULL AND gateway_request_id IS NULL)
            OR (app_id IS NOT NULL AND app_id ~ '^[a-z][a-z0-9-]{0,63}$'
                AND release_version IS NOT NULL AND release_version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
                AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'::uuid
                AND gateway_request_id IS NOT NULL AND gateway_request_id ~ '^[a-f0-9]{32}$')) IS TRUE);
    INSERT INTO public.platform_schema_migrations(version,filename) VALUES(7,'0007_model_call_source.sql');
END $migration$;
COMMIT;
