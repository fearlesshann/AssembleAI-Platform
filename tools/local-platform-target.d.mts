import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Duplex } from 'node:stream';

export function localPlatformTarget(value?: string): URL | undefined;
export function isPlatformRequest(path?: string): boolean;
export function createPlatformProxy(platformOrigin?: string): (req: IncomingMessage, res: ServerResponse) => boolean;
export function rejectPlatformUpgrade(req: IncomingMessage, socket: Duplex): boolean;
