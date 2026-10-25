const fs = require('node:fs');
const path = require('node:path');
const web = path.resolve(__dirname, '../../src/Plugins.UniversalForward/Web');
module.exports = () => fs.readFileSync(path.join(web, 'index.html'), 'utf8')
  .replace('/*__APPEARANCE__*/', () => fs.readFileSync(path.join(web, 'appearance.js'), 'utf8'))
  .replace('/*__STYLE__*/', () => fs.readFileSync(path.join(web, 'styles.css'), 'utf8'))
  .replace('/*__SCRIPT__*/', () => fs.readFileSync(path.join(web, 'app.js'), 'utf8')
    .replace('/*__SETTINGS__*/', () => fs.readFileSync(path.join(web, 'settings.js'), 'utf8'))
    .replace('/*__REASONING__*/', () => fs.readFileSync(path.join(web, 'reasoning.js'), 'utf8'))
    .replace('/*__JOURNAL__*/', () => fs.readFileSync(path.join(web, 'journal.js'), 'utf8')));
