import rss from '@astrojs/rss';
import { getCollection } from 'astro:content';
import type { APIContext } from 'astro';

export async function GET(context: APIContext) {
  const stories = await getCollection('stories');
  const sorted = stories.sort(
    (a, b) => new Date(b.data.date_published).getTime() - new Date(a.data.date_published).getTime()
  );

  return rss({
    title: 'Authosy - Uplifting Verified News',
    description: 'Good things are happening. We find them, verify them, and share them with you.',
    site: context.site!.toString(),
    items: sorted.map((story) => ({
      title: story.data.title,
      pubDate: new Date(story.data.date_published),
      description: story.data.summary,
      link: `/authosy/stories/${story.id.replace(/\.md$/, '')}/`,
      categories: story.data.tags,
    })),
    customData: '<language>en-us</language>',
  });
}
