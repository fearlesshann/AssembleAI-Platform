-- Explicit, first-administrator bootstrap. Not part of apply.sql.
-- Required psql variables: user_id, issuer, subject, display_name.
-- Supply database credentials through PGPASSWORD or a password file, never arguments.
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '5s';
DO $guard$
BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Identity bootstrap requires assembleai_platform';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_schema_migrations WHERE version = 12) THEN
        RAISE EXCEPTION 'Identity bootstrap requires migration 12';
    END IF;
END
$guard$;
SET LOCAL ROLE assembleai_platform;
-- psql quoted-literal interpolation treats operator input as data, including quotes.
CREATE TEMPORARY TABLE identity_bootstrap_input ON COMMIT DROP AS
    SELECT :'user_id'::uuid AS user_id, :'issuer'::text AS issuer,
        :'subject'::text AS subject, :'display_name'::text AS display_name;

-- Same relative order as the identity management transaction: users before admins.
LOCK TABLE public.platform_users, public.platform_identity_administrators
    IN SHARE ROW EXCLUSIVE MODE;

DO $bootstrap$
DECLARE
    proposed record;
    current_user_row record;
BEGIN
    SELECT * INTO STRICT proposed FROM identity_bootstrap_input;
    IF proposed.user_id = '00000000-0000-0000-0000-000000000000'
        OR length(proposed.issuer) NOT BETWEEN 1 AND 2048
        OR proposed.issuer !~ '^https?://[^/?#@]+(/[^?#]*)?$'
        OR proposed.issuer <> btrim(proposed.issuer)
        OR length(proposed.subject) NOT BETWEEN 1 AND 512
        OR proposed.subject <> btrim(proposed.subject)
        OR length(proposed.display_name) NOT BETWEEN 1 AND 256
        OR proposed.display_name <> btrim(proposed.display_name)
        OR proposed.issuer ~ '[[:cntrl:]]' OR proposed.subject ~ '[[:cntrl:]]'
        OR proposed.display_name ~ '[[:cntrl:]]' THEN
        RAISE EXCEPTION 'Invalid identity bootstrap input';
    END IF;

    SELECT * INTO current_user_row FROM public.platform_users WHERE user_id = proposed.user_id;
    IF FOUND AND (current_user_row.issuer <> proposed.issuer
        OR current_user_row.subject <> proposed.subject OR NOT current_user_row.enabled) THEN
        RAISE EXCEPTION 'Existing user binding or enabled state does not match';
    END IF;
    IF EXISTS (SELECT 1 FROM public.platform_users WHERE issuer = proposed.issuer
        AND subject = proposed.subject AND user_id <> proposed.user_id) THEN
        RAISE EXCEPTION 'Identity is already bound to another user identifier';
    END IF;

    IF EXISTS (SELECT 1 FROM public.platform_identity_administrators a
        JOIN public.platform_users u USING (user_id)
        WHERE a.user_id = proposed.user_id AND a.enabled AND u.enabled) THEN
        -- Exact repeat: do not overwrite names, revisions, or add duplicate audit.
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM public.platform_identity_administrators a
        JOIN public.platform_users u USING (user_id) WHERE a.enabled AND u.enabled) THEN
        RAISE EXCEPTION 'A platform identity administrator already exists; use the management UI';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.platform_users WHERE user_id = proposed.user_id)
        AND (SELECT count(*) FROM public.platform_users) >= 500 THEN
        RAISE EXCEPTION 'Identity management user capacity has been reached';
    END IF;

    INSERT INTO public.platform_users(user_id, issuer, subject, display_name)
        VALUES (proposed.user_id, proposed.issuer, proposed.subject, proposed.display_name)
        ON CONFLICT (user_id) DO NOTHING;
    INSERT INTO public.platform_identity_administrators(user_id, enabled)
        VALUES (proposed.user_id, true)
        ON CONFLICT (user_id) DO UPDATE SET enabled = true;
    UPDATE public.platform_users SET identity_revision = identity_revision + 1
        WHERE user_id = proposed.user_id;
    -- Match IdentityResponseBudget: bounded JSON includes escaping and audit headroom.
    -- Check after the mutation so any failure rolls back the entire bootstrap.
    IF 512 + 524288
        + COALESCE((SELECT SUM(512::bigint + 6::bigint *
            (octet_length(issuer) + octet_length(subject) + octet_length(display_name)))
            FROM public.platform_users), 0)
        + COALESCE((SELECT SUM(512::bigint + 6::bigint * octet_length(name))
            FROM public.platform_tenants), 0) > 4194304 THEN
        RAISE EXCEPTION 'Identity management response capacity has been reached';
    END IF;
    INSERT INTO public.platform_identity_audit(event_id, actor_user_id, tenant_id,
        entity_type, entity_id, action, result, request_id)
        VALUES (gen_random_uuid(), proposed.user_id, NULL, 'user', proposed.user_id,
            'bootstrap_admin', 'success', 'bootstrap-' || gen_random_uuid()::text);
END
$bootstrap$;
COMMIT;
