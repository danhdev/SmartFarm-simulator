// The current local deployment allows bootstrap access through transitionToAuth.
// No change to its authentication mode, replica set, or profiling level.
const admin = db.getSiblingDB('admin');
const password = process.env.MONGO_DATADOG_PASSWORD;
if (!password) throw new Error('Monitoring password is required');
const roles = [
  { role: 'clusterMonitor', db: 'admin' },
  { role: 'read', db: 'admin' },
  { role: 'read', db: 'local' },
  { role: 'read', db: 'MUGVN-Hackathon' },
];
if (admin.getUser('datadog')) admin.updateUser('datadog', { pwd: password, roles });
else admin.createUser({ user: 'datadog', pwd: password, roles });
assert.commandWorked(admin.runCommand({ setParameter: 1, internalQueryStatsSampleRate: 1.0 }));
