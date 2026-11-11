/** Response helpers that reproduce ASP.NET's `Results.Problem` / `Results.Unauthorized` shapes. */

const PROBLEM_TYPES: Record<number, { type: string; title: string }> = {
  400: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1', title: 'Bad Request' },
  401: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2', title: 'Unauthorized' },
  413: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.14', title: 'Request Entity Too Large' },
  502: { type: 'https://tools.ietf.org/html/rfc9110#section-15.6.3', title: 'Bad Gateway' },
  503: { type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4', title: 'Service Unavailable' },
};

/** An RFC 9457 problem+json response with `detail`, like `Results.Problem(detail, statusCode: status)`. */
export function problem(status: number, detail: string, headers?: Record<string, string>): Response {
  const known = PROBLEM_TYPES[status] ?? { type: 'about:blank', title: 'Error' };
  return new Response(JSON.stringify({ type: known.type, title: known.title, status, detail }), {
    status,
    headers: { 'content-type': 'application/problem+json', ...headers },
  });
}

/** A bare 401, like `Results.Unauthorized()`. */
export function unauthorized(): Response {
  return new Response(null, { status: 401 });
}

export function json(body: unknown, status = 200, headers?: Record<string, string>): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
}

/** UTF-8 byte length of a string. */
export function byteLength(value: string): number {
  return new TextEncoder().encode(value).length;
}

export type BodyResult = { ok: true; value: unknown } | { ok: false; response: Response };

/**
 * Reads a JSON request body, refusing one over `limit` bytes with a 413 before parsing it. The
 * Content-Length check refuses an honest oversized request without reading a byte; the length of
 * what was actually read covers a chunked upload that declared nothing.
 */
export async function readJsonBody(request: Request, limit: number): Promise<BodyResult> {
  const tooLarge = (): BodyResult => ({ ok: false, response: problem(413, `The request body exceeds the ${limit} byte limit.`) });

  const declared = Number(request.headers.get('content-length'));
  if (Number.isFinite(declared) && declared > limit) return tooLarge();

  // Count bytes as they arrive and stop reading the moment the limit is passed, so an undeclared
  // (chunked) upload cannot make the isolate buffer far more than it will ever accept.
  const chunks: Uint8Array[] = [];
  let total = 0;
  if (request.body) {
    const reader = request.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > limit) {
        await reader.cancel();
        return tooLarge();
      }
      chunks.push(value);
    }
  }
  if (total === 0) return { ok: true, value: undefined };

  const buffer = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    buffer.set(chunk, offset);
    offset += chunk.byteLength;
  }

  try {
    return { ok: true, value: JSON.parse(new TextDecoder().decode(buffer)) };
  } catch {
    return { ok: false, response: problem(400, 'The request body is not valid JSON.') };
  }
}

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
