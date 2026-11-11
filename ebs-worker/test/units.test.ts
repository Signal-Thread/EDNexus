import { describe, expect, it } from 'vitest';
import { loadConfig } from '../src/config';
import { decryptText, DecryptionError, encryptText, pkceChallenge, timingSafeEqual, verifyPkce } from '../src/crypto';
import { TwitchOAuthError } from '../src/twitch/oauth';
import { ENCRYPTION_KEY, b64, makeEnv } from './helpers';

describe('encryption at rest', () => {
  it('round-trips and never repeats a ciphertext', async () => {
    const a = await encryptText(ENCRYPTION_KEY, 'secret', 'chan');
    const b = await encryptText(ENCRYPTION_KEY, 'secret', 'chan');
    expect(a).not.toBe(b);
    expect(a).not.toContain('secret');
    expect(await decryptText(ENCRYPTION_KEY, a, 'chan')).toBe('secret');
  });

  it('refuses a ciphertext moved to another row, a different key, or a tampered value', async () => {
    const sealed = await encryptText(ENCRYPTION_KEY, 'secret', 'chan-1');
    await expect(decryptText(ENCRYPTION_KEY, sealed, 'chan-2')).rejects.toBeInstanceOf(DecryptionError);
    await expect(decryptText(b64(32, 1), sealed, 'chan-1')).rejects.toBeInstanceOf(DecryptionError);
    await expect(decryptText(ENCRYPTION_KEY, sealed.slice(0, -2) + 'AA', 'chan-1')).rejects.toBeInstanceOf(DecryptionError);
    await expect(decryptText(ENCRYPTION_KEY, 'plaintext', 'chan-1')).rejects.toBeInstanceOf(DecryptionError);
  });
});

describe('PKCE', () => {
  it('matches the RFC 7636 appendix B vector', async () => {
    expect(await pkceChallenge('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
  });

  it('verifies, and refuses empty and mismatched values', async () => {
    const challenge = await pkceChallenge('verifier');
    expect(await verifyPkce('verifier', challenge)).toBe(true);
    expect(await verifyPkce('other', challenge)).toBe(false);
    expect(await verifyPkce('', challenge)).toBe(false);
    expect(await verifyPkce('verifier', '')).toBe(false);
  });

  it('compares in a way that is length-aware', () => {
    expect(timingSafeEqual('abc', 'abc')).toBe(true);
    expect(timingSafeEqual('abc', 'abd')).toBe(false);
    expect(timingSafeEqual('abc', 'abcd')).toBe(false);
  });
});

describe('TwitchOAuthError.isGrantRejection', () => {
  it.each([
    [400, 'Invalid refresh token', true],
    [401, 'invalid_grant', true],
    [400, 'invalid client secret', false],
    [400, 'Invalid Client', false],
    [429, 'slow down', false],
    [500, 'oops', false],
    [null, 'Unparseable body', false],
  ])('%s %s -> %s', (status, message, expected) => {
    expect(new TwitchOAuthError(message, status).isGrantRejection).toBe(expected);
  });
});

describe('configuration validation', () => {
  const load = (overrides = {}) => loadConfig(makeEnv(overrides).env);
  const failures = (overrides = {}) => {
    const result = load(overrides);
    return result.ok ? [] : result.failures;
  };

  it('accepts a complete configuration and applies the defaults', () => {
    const result = load();
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config).toMatchObject({
      maxStatePayloadBytes: 5000,
      maxRequestBodyBytes: 16384,
      updateStateRateLimit: 1,
      updateStateRateLimitWindowSeconds: 2,
      initialStateCacheSeconds: 5,
      oauthSessionTtlMinutes: 10,
      oauthCodeTtlSeconds: 60,
      tokenRefreshBufferMinutes: 60,
      channelStateMaxAgeHours: 24,
      oauthScopes: ['user:read:email'],
      ownerUserId: null,
    });
  });

  it('reports every problem at once, naming the setting', () => {
    const list = failures({
      TWITCH_CLIENT_ID: '',
      TWITCH_CLIENT_SECRET: '',
      TWITCH_EXTENSION_ID: '',
      TWITCH_EXTENSION_SECRET: '',
      TOKEN_ENCRYPTION_KEY: '',
    });
    expect(list).toHaveLength(5);
    for (const name of ['TWITCH_CLIENT_ID', 'TWITCH_CLIENT_SECRET', 'TWITCH_EXTENSION_ID', 'TWITCH_EXTENSION_SECRET', 'TOKEN_ENCRYPTION_KEY'])
      expect(list.some((f) => f.includes(name))).toBe(true);
  });

  it.each([
    ['a non-base64 extension secret', { TWITCH_EXTENSION_SECRET: 'not base64!' }, 'TWITCH_EXTENSION_SECRET'],
    ['an extension secret under 16 bytes', { TWITCH_EXTENSION_SECRET: b64(15, 1) }, 'only 15 bytes'],
    ['an encryption key that is not 32 bytes', { TOKEN_ENCRYPTION_KEY: b64(16, 1) }, 'exactly 32 bytes'],
    ['an http redirect URI that is not localhost', { TWITCH_OAUTH_REDIRECT_URI: 'http://ebs.example.test/cb' }, 'TWITCH_OAUTH_REDIRECT_URI'],
    ['a relative redirect URI', { TWITCH_OAUTH_REDIRECT_URI: '/oauth/callback' }, 'TWITCH_OAUTH_REDIRECT_URI'],
    ['a non-numeric owner id', { TWITCH_OWNER_USER_ID: 'abc' }, 'TWITCH_OWNER_USER_ID'],
    ['a payload limit above 5 KiB', { MAX_STATE_PAYLOAD_BYTES: '5121' }, 'MAX_STATE_PAYLOAD_BYTES'],
    ['a request cap under 1024', { MAX_REQUEST_BODY_BYTES: '100' }, 'MAX_REQUEST_BODY_BYTES'],
    ['a request cap with no room for the state', { MAX_REQUEST_BODY_BYTES: '4096' }, 'leave room'],
    ['a snapshot lifetime of 1-11 hours', { CHANNEL_STATE_MAX_AGE_HOURS: '11' }, 'CHANNEL_STATE_MAX_AGE_HOURS'],
    ['a setting that is not a number', { MAX_STATE_PAYLOAD_BYTES: 'abc' }, 'must be a whole number'],
  ])('refuses %s', (_name, overrides, fragment) => {
    expect(failures(overrides).join('\n')).toContain(fragment);
  });

  it.each([
    ['http for localhost', { TWITCH_OAUTH_REDIRECT_URI: 'http://localhost:8787/oauth/callback' }],
    ['a snapshot lifetime of 0 (no limit)', { CHANNEL_STATE_MAX_AGE_HOURS: '0' }],
    ['a snapshot lifetime of 12 hours', { CHANNEL_STATE_MAX_AGE_HOURS: '12' }],
    ['a numeric owner id', { TWITCH_OWNER_USER_ID: '12345' }],
  ])('accepts %s', (_name, overrides) => {
    expect(failures(overrides)).toEqual([]);
  });
});
