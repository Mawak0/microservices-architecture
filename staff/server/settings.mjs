export function settings(env) {
  function required(name) {
    if (!env[name]) throw new Error('Configure ' + name);
    return env[name];
  }

  const origin = new URL(required('STAFF_ORIGIN'));
  if (origin.href !== origin.origin + '/') throw new Error('Configure an origin without a path');
  if (origin.protocol !== 'https:' && !['localhost', '127.0.0.1'].includes(origin.hostname))
    throw new Error('The staff origin requires HTTPS');

  return {
    origin,
    identityUrl: required('STAFF_IDENTITY_URL'),
    directoryUrl: required('STAFF_DIRECTORY_URL'),
    identityServiceToken: env.STAFF_IDENTITY_SERVICE_TOKEN,
    services: {
      materials: required('STAFF_MATERIALS_URL'),
      intake: required('STAFF_INTAKE_URL'),
      review: required('STAFF_REVIEW_URL'),
    },
    oidc: {
      issuer: required('STAFF_OIDC_ISSUER'),
      clientId: required('STAFF_OIDC_CLIENT_ID'),
      clientSecret: env.STAFF_OIDC_CLIENT_SECRET,
      scope: env.STAFF_OIDC_SCOPE,
    },
    port: Number(env.STAFF_PORT ?? 19184),
  };
}
