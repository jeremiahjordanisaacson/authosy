import { getCollection } from 'astro:content';
import type { APIContext } from 'astro';

export async function GET(_context: APIContext) {
  const stories = await getCollection('stories');
  const index = stories.map((story) => ({
    slug: story.id.replace(/\.md$/, ''),
    title: story.data.title,
    region: story.data.region,
    date_published: story.data.date_published,
    summary: story.data.summary,
    tags: story.data.tags,
  }));

  return new Response(JSON.stringify(index), {
    headers: { 'Content-Type': 'application/json' },
  });
}
