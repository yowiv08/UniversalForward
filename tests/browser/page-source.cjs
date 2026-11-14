const fs = require('node:fs');
const path = require('node:path');
const web = path.resolve(__dirname, '../../src/Plugins.UniversalForward/Web');
const modules = [
  ['/*__CORE__*/', 'core.js'],
  ['/*__HEADERS_MAPPING__*/', 'headers-mapping.js'],
  ['/*__SETTINGS__*/', 'settings.js'],
  ['/*__REASONING__*/', 'reasoning.js'],
  ['/*__CONNECTION_TESTS__*/', 'connection-tests.js'],
  ['/*__JOURNAL__*/', 'journal.js']
];

module.exports = () => {
  let script = fs.readFileSync(path.join(web, 'app.js'), 'utf8');
  for (const [token, file] of modules) {
    if (!script.includes(token)) throw Error('页面缺少模块入口：' + file);
    script = script.replace(token, () => fs.readFileSync(path.join(web, file), 'utf8'));
  }
  return fs.readFileSync(path.join(web, 'index.html'), 'utf8')
    .replace('/*__APPEARANCE__*/', () => fs.readFileSync(path.join(web, 'appearance.js'), 'utf8'))
    .replace('/*__STYLE__*/', () => fs.readFileSync(path.join(web, 'styles.css'), 'utf8'))
    .replace('/*__SCRIPT__*/', () => script);
};
