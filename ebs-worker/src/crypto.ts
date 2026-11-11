/** WebCrypto helpers: base64url, SHA-256, HMAC, PKCE, and AES-GCM for tokens at rest. */

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function base64UrlEncode(bytes: Uint8Array): string {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function base64UrlDecode(value: string): Uint8Array {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(padded + '='.repeat((4 - (padded.length % 4)) % 4));
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}

export function base64Decode(value: string): Uint8Array {
  return Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
}

/** A random, URL-safe opaque token of `byteLength` random bytes. */
export function randomToken(byteLength = 32): string {
  return base64UrlEncode(crypto.getRandomValues(new Uint8Array(byteLength)));
}

/** Byte length of long-lived broadcaster tokens, and of short-lived session ids and codes. */
export const TOKEN_BYTES = 32;
export const SHORT_LIVED_BYTES = 24;

export async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode(value));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function hmacSha256(key: Uint8Array, data: Uint8Array): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', cryptoKey, data));
}

/** Constant-time comparison of two strings (length differences are not hidden; they are not secret here). */
export function timingSafeEqual(a: string, b: string): boolean {
  const x = encoder.encode(a);
  const y = encoder.encode(b);
  if (x.length !== y.length) return false;
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x[i]! ^ y[i]!;
  return diff === 0;
}

/** RFC 7636 S256 challenge for a verifier. */
export async function pkceChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode(verifier));
  return base64UrlEncode(new Uint8Array(digest));
}

/** Verifies a PKCE verifier against the recorded challenge in constant time. */
export async function verifyPkce(verifier: string, challenge: string): Promise<boolean> {
  if (!verifier || !challenge) return false;
  return timingSafeEqual(await pkceChallenge(verifier), challenge);
}

// ---------------------------------------------------------------------------------------------
// Encryption of Twitch tokens at rest. The ASP.NET build used Data Protection with a key ring kept
// on a separate volume; here the key is a Worker secret, so a copy of the D1 database alone cannot be
// decrypted. Format: "v1.<iv>.<ciphertext+tag>", both base64url. `context` (the row it belongs to) is
// bound in as additional authenticated data, so a ciphertext cannot be replayed into another row.
// ---------------------------------------------------------------------------------------------

const FORMAT = 'v1';

export class DecryptionError extends Error {}

async function importKey(base64Key: string, usage: 'encrypt' | 'decrypt'): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', base64Decode(base64Key), 'AES-GCM', false, [usage]);
}

export async function encryptText(base64Key: string, plaintext: string, context: string): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const key = await importKey(base64Key, 'encrypt');
  const sealed = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: encoder.encode(context) }, key, encoder.encode(plaintext));
  return `${FORMAT}.${base64UrlEncode(iv)}.${base64UrlEncode(new Uint8Array(sealed))}`;
}

export async function decryptText(base64Key: string, sealed: string, context: string): Promise<string> {
  const [format, iv, data] = sealed.split('.');
  if (format !== FORMAT || !iv || !data) throw new DecryptionError('Unrecognised ciphertext format.');
  try {
    const key = await importKey(base64Key, 'decrypt');
    const plain = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: base64UrlDecode(iv), additionalData: encoder.encode(context) },
      key,
      base64UrlDecode(data),
    );
    return decoder.decode(plain);
  } catch (error) {
    throw new DecryptionError(error instanceof Error ? error.message : 'Decryption failed.');
  }
}
