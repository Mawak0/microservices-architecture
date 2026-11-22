const validId = value => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,200}$/.test(value);

export function directoryContains(directory, gameId) {
  if (!Array.isArray(directory?.tournaments)) throw new Error('Invalid staff directory');
  return directory.tournaments.some(tournament =>
    tournament.games.some(game => game.id === gameId));
}

function field(bytes, name) {
  try {
    const fields = Object.entries(JSON.parse(bytes.toString('utf8')))
      .filter(([key]) => key.toLowerCase() === name.toLowerCase());
    return fields.length === 1 ? fields[0][1] : undefined;
  } catch { return undefined; }
}

export async function belongsToGame({ service, segment, rest, method, url, bytes, gameId }, lookup) {
  const [id] = rest;
  if (service === 'review' && segment === 'appeal-quotas')
    return rest.length === 1 && id === gameId;

  const checkList = service === 'review' && segment === 'checks' &&
    rest.length === 1 && (id === 'activity' || id === 'search');
  if (method === 'GET' && (!id || checkList)) {
    const games = [...url.searchParams].filter(([name]) => name.toLowerCase() === 'gameid');
    return games.length === 1 && games[0][1] === gameId;
  }

  async function checkInGame(checkId) {
    if (!validId(checkId)) return false;
    const params = new URLSearchParams({ gameId, id: checkId, limit: '1' });
    const result = await lookup('checks/search?' + params);
    return result?.items?.some(item => item.id === checkId && item.gameId === gameId) ?? false;
  }

  if (method === 'POST') {
    if (service === 'review' && segment === 'appeals' && !id)
      return await checkInGame(field(bytes, 'checkId'));
    const next = service === 'review' && (segment === 'checks' || segment === 'appeals') && id === 'next';
    const preview = service === 'intake' && segment === 'receipts' && id === 'preview';
    if (!id || (rest.length === 1 && (next || preview)))
      return field(bytes, 'gameId') === gameId;
  }

  if (!validId(id)) return false;
  if (service === 'review' && segment === 'checks') return await checkInGame(id);
  const resource = await lookup(segment + '/' + id);
  return (service === 'review' && segment === 'appeals' ? resource?.check?.gameId : resource?.gameId) === gameId;
}
