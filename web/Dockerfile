FROM oven/bun:1 AS deps
WORKDIR /app
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile

FROM oven/bun:1 AS build
WORKDIR /app
# Canonical site identity baked into the image (metadataBase/robots/sitemap). release-images.yml
# passes no build args, so this ARG default is what the published GHCR image ships with.
ARG NEXT_PUBLIC_SITE_URL=https://sol.routin.ai/
ARG API_BASE_URL=http://localhost:5298
# next.config.ts resolves this into routes-manifest.json at build time, so it must be a
# build arg — setting it only in the runtime environment is too late for rewrites.
ARG SOL_API_ORIGIN=http://localhost:5298
ENV NEXT_PUBLIC_SITE_URL=$NEXT_PUBLIC_SITE_URL
ENV API_BASE_URL=$API_BASE_URL
ENV SOL_API_ORIGIN=$SOL_API_ORIGIN
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN bun run build

# .next/standalone bundles only the traced production dependencies, so the runtime image
# needs no node_modules/bun install step.
FROM oven/bun:1-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /app/public ./public
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
EXPOSE 3000
ENTRYPOINT ["bun", "server.js"]
