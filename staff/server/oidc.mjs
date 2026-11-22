import * as oidc from 'openid-client';

export async function connectIdentity({ issuer, clientId, clientSecret, scope = 'openid' }, library = oidc) {
  const configuration = await library.discovery(new URL(issuer), clientId, clientSecret);
  return {
    async begin(redirectUri) {
      const verifier = library.randomPKCECodeVerifier();
      const state = library.randomState();
      const url = library.buildAuthorizationUrl(configuration, {
        scope, redirect_uri: redirectUri, state,
        code_challenge: await library.calculatePKCECodeChallenge(verifier),
        code_challenge_method: 'S256',
      });
      return { url: url.href, verifier, state };
    },
    async finish(url, challenge) {
      const tokens = await library.authorizationCodeGrant(configuration, url, {
        pkceCodeVerifier: challenge.verifier,
        expectedState: challenge.state,
      });
      if (!tokens.access_token) throw new Error('Missing access token');
      return { token: tokens.access_token, lifetime: tokens.expires_in ?? 3600 };
    },
  };
}
