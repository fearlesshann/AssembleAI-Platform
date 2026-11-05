import { spawn } from 'node:child_process';
import { parseArgs } from 'node:util';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const schema = fileURLToPath(new URL('../deploy/postgres/bootstrap-identity-admin.sql', import.meta.url));
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const validText = (value, max) => typeof value === 'string' && value.length > 0
  && Array.from(value).length <= max && value.trim() === value && !/\p{Cc}/u.test(value);

// Native Windows psql receives ANSI argv, even when PGCLIENTENCODING is UTF8.
// Keep metadata out of its argv: base64 is ASCII SQL data, decoded by PostgreSQL.
// \gset produces psql variables without constructing a quoted meta-command.
export function bootstrapInput(options) {
  if (!uuid.test(options['user-id'] ?? '') || options['user-id'] === '00000000-0000-0000-0000-000000000000'
    || !validText(options.issuer, 2048) || !validText(options.subject, 512) || !validText(options['display-name'], 256))
    throw new Error('Provide a nonempty user UUID, issuer, subject and display name.');
  let issuer;
  try { issuer = new URL(options.issuer); } catch { throw new Error('Issuer must be an absolute HTTP or HTTPS URL.'); }
  if (!['https:', 'http:'].includes(issuer.protocol) || issuer.username || issuer.password || issuer.search || issuer.hash)
    throw new Error('Issuer cannot contain credentials, a query or a fragment.');
  const literal = value => `convert_from(decode('${Buffer.from(value, 'utf8').toString('base64')}','base64'),'UTF8')`;
  return `SELECT ${literal(options['user-id'])} AS user_id, ${literal(options.issuer)} AS issuer, ${literal(options.subject)} AS subject, ${literal(options['display-name'])} AS display_name\n\\gset\n`;
}

export async function bootstrapAdministrator(options, environment = process.env) {
  const input = bootstrapInput(options);
  const port = options.port ?? '5432';
  if (!/^[0-9]{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535)
    throw new Error('Database port must be between 1 and 65535.');
  const host = options.host ?? '127.0.0.1';
  const username = options.username ?? 'postgres';
  if (![host, username].every(value => /^[\x21-\x7e]+$/.test(value)))
    throw new Error('Database host and role must use ASCII characters.');
  const args = ['--no-psqlrc', '--no-password', '--host', host, '--port', port, '--username', username,
    '--dbname', 'assembleai_platform', '--set', 'ON_ERROR_STOP=1', '--file', '-', '--file', schema];
  return new Promise((accept, reject) => {
    const child = spawn(options.psql ?? 'psql', args, {
      shell: false, windowsHide: true, stdio: ['pipe', 'ignore', 'pipe'],
      env: { ...environment, PGCLIENTENCODING: 'UTF8', PGCONNECT_TIMEOUT: '5' },
    });
    let expired = false;
    const timer = setTimeout(() => { expired = true; child.kill(); }, 20_000);
    // Discard driver diagnostics: they may include identity metadata or a server detail.
    child.stderr.resume();
    child.stdin.on('error', () => {}); // Early psql failure is reported by close/error below.
    child.once('error', () => { clearTimeout(timer); reject(new Error('Could not start psql. Supply its executable with --psql.')); });
    child.once('close', code => {
      clearTimeout(timer);
      if (code === 0 && !expired) accept();
      else reject(new Error(expired ? 'Administrator initialization timed out; inspect the database before retrying.'
        : 'Administrator initialization failed. Check connectivity, migration 12, identity binding and existing administrators.'));
    });
    child.stdin.end(input, 'ascii');
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  try {
    const { values } = parseArgs({ options: Object.fromEntries(
      ['psql', 'host', 'port', 'username', 'user-id', 'issuer', 'subject', 'display-name'].map(name => [name, { type: 'string' }])) });
    await bootstrapAdministrator(values);
    console.log('Platform identity administrator initialized or already active.');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
