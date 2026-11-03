import type { IncomingMessage, ServerResponse } from 'node:http';

export interface FrontendArtifactConfiguration {
  artifacts: { appId: string; version: string; root: string; sha256: string }[];
}
export interface FrontendArtifactMetadata {
  readonly appId: string;
  readonly version: string;
  readonly entry: string;
  readonly sha256: string;
  readonly fileCount: number;
  readonly totalBytes: number;
}
export interface FrontendArtifacts {
  readonly artifacts: readonly FrontendArtifactMetadata[];
  handle(req: IncomingMessage, res: ServerResponse): boolean;
}
export function frontendArtifactEntry(appId: string, version: string): string;
export function frontendArtifactDigest(root: string): Readonly<{ sha256: string; fileCount: number; totalBytes: number }>;
export function createFrontendArtifacts(configuration: FrontendArtifactConfiguration, options?: { baseDirectory?: string }): FrontendArtifacts;
export function loadFrontendArtifacts(configurationPath: string): FrontendArtifacts;
export function developmentFrontendArtifacts(sampleRoot?: string): FrontendArtifacts;
