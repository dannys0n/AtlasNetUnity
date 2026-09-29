module.exports = {
  docs: [
    'intro',
    {type: 'category', label: 'Get started', items: ['getting-started/installation', 'getting-started/first-session']},
    'concepts/authority',
    {type: 'category', label: 'Build gameplay', items: [
      'guides/prefabs-components', 'guides/movement', 'guides/network-variables',
      'guides/rpcs', 'guides/spawning', 'guides/physics-animation',
    ]},
    'guides/interest-handoffs',
    'reference/api',
    'troubleshooting',
  ],
};
