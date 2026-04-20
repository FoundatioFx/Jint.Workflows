import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Jint.Workflows',
  description: 'Durable JavaScript workflows for .NET using Jint',
  base: '/',
  ignoreDeadLinks: true,
  head: [
    ['link', { rel: 'icon', href: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/main/media/foundatio-icon.png', type: 'image/png' }],
    ['meta', { name: 'theme-color', content: '#3c8772' }]
  ],
  themeConfig: {
    logo: {
      light: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg',
      dark: 'https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg'
    },
    siteTitle: 'Jint.Workflows',
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'GitHub', link: 'https://github.com/FoundatioFx/Jint.Workflows' }
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Introduction',
          items: [
            { text: 'Getting Started', link: '/guide/getting-started' },
            { text: 'How It Works', link: '/guide/how-it-works' }
          ]
        },
        {
          text: 'Building Workflows',
          items: [
            { text: 'Step Functions', link: '/guide/step-functions' },
            { text: 'Suspending Execution', link: '/guide/suspending' },
            { text: 'HTTP with fetch', link: '/guide/fetch' },
            { text: 'Continue As New', link: '/guide/continue-as-new' }
          ]
        },
        {
          text: 'Operating Workflows',
          items: [
            { text: 'Versioning', link: '/guide/versioning' }
          ]
        }
      ]
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/FoundatioFx/Jint.Workflows' },
      { icon: 'discord', link: 'https://discord.gg/6HxgFCx' }
    ],
    footer: {
      message: 'Released under the Apache-2.0 License.',
      copyright: 'Copyright © 2026 Foundatio'
    },
    editLink: {
      pattern: 'https://github.com/FoundatioFx/Jint.Workflows/edit/main/docs/:path'
    },
    search: {
      provider: 'local'
    }
  }
})
