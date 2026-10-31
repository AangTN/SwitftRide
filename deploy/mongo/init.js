const matching = db.getSiblingDB('MatchingDB');
matching.createUser({
  user: 'matching_app',
  pwd: process.env.MATCHING_DB_PASSWORD,
  roles: [{ role: 'readWrite', db: 'MatchingDB' }]
});
