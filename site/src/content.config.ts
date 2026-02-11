import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

const stories = defineCollection({
  loader: glob({ pattern: '**/*.md', base: './src/content/stories' }),
  schema: z.object({
    id: z.string(),
    title: z.string(),
    region: z.enum(['seattle', 'usa', 'world']),
    date_published: z.string(),
    summary: z.string(),
    tags: z.array(z.string()),
    hero_image: z.string().optional(),
    source_urls: z.array(z.string()),
    positivity_score: z.number().min(0).max(1),
    confidence_score: z.number().min(0).max(1),
  }),
});

export const collections = { stories };
