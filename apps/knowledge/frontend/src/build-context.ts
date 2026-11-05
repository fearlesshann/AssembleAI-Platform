declare const __ASSEMBLEAI_APP_ID__: string;
declare const __ASSEMBLEAI_FRONTEND_VERSION__: string;
export const BUILD_APP_ID = typeof __ASSEMBLEAI_APP_ID__ === 'undefined' ? 'knowledge' : __ASSEMBLEAI_APP_ID__;
export const BUILD_FRONTEND_VERSION = typeof __ASSEMBLEAI_FRONTEND_VERSION__ === 'undefined' ? '0.1.0' : __ASSEMBLEAI_FRONTEND_VERSION__;
