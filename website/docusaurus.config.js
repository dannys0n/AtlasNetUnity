const {themes} = require('prism-react-renderer');

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'AtlasNet Unity',
  tagline: 'Familiar Unity networking. A world shared by workers.',
  url: 'https://dannys0n.github.io',
  baseUrl: '/AtlasNetUnity/',
  organizationName: 'dannys0n',
  projectName: 'AtlasNetUnity',
  trailingSlash: true,
  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',
  markdown: {hooks: {onBrokenMarkdownLinks: 'throw'}},
  i18n: {defaultLocale: 'en', locales: ['en']},
  presets: [
    ['classic', {
      docs: {
        routeBasePath: '/',
        sidebarPath: require.resolve('./sidebars.js'),
        editUrl: 'https://github.com/dannys0n/AtlasNetUnity/edit/main/website/',
        showLastUpdateTime: false,
      },
      blog: false,
      theme: {customCss: require.resolve('./src/css/custom.css')},
    }],
  ],
  themeConfig: {
    image: 'img/shooter-cross-server.jpg',
    colorMode: {defaultMode: 'dark', respectPrefersColorScheme: true},
    navbar: {
      title: 'AtlasNet Unity',
      items: [
        {type: 'doc', docId: 'getting-started/installation', label: 'Get started', position: 'left'},
        {type: 'doc', docId: 'reference/api', label: 'API reference', position: 'left'},
        {href: 'https://github.com/dannys0n/AtlasNetUnity', label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {title: 'Learn', items: [
          {label: 'Install the package', to: '/getting-started/installation'},
          {label: 'Authority and ownership', to: '/concepts/authority'},
        ]},
        {title: 'Project', items: [
          {label: 'Unity package source', href: 'https://github.com/dannys0n/AtlasNetUnity'},
          {label: 'Shooter demo', href: 'https://github.com/dannys0n/AtlasNet-Unity-ShooterDemo'},
          {label: 'Maintain these docs', to: '/maintaining-docs'},
        ]},
      ],
      copyright: 'AtlasNet Unity · Experimental local-backend documentation',
    },
    prism: {theme: themes.github, darkTheme: themes.dracula, additionalLanguages: ['csharp', 'powershell']},
    tableOfContents: {minHeadingLevel: 2, maxHeadingLevel: 3},
  },
};

module.exports = config;
