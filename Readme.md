## What is this?

This is a lightweight Azure function that analyzes a URL and finds its web manifest.

## How is it used in PWABuilder?

PWABuilder.com uses [Puppeteer](https://developers.google.com/web/tools/puppeteer/), a headless Chrome engine, to load and analyze URLs for web manifest, service worker, HTTPS, and more.

However, when Puppeteer fails to fetch a web app's manifest, we use this function as a fallback.

## How to use it

Issue a GET to `/api/FindManifest?url=https://somepwa.com`, where somepwa.com is a URL that has a web manifest.

The response will be a JSON object containing:

```typescript
{
	manifestUrl: string | null,
    manifestContents: string | null,
	error: string | null
}
```

- **manifestUrl** - the URL to the web manifest. This will be null if there was an error fetching the web app or its manifest.
- **manifestContents** - the string contents of the web manifest. This will be null if there was an error fetching the web app or its manifest.
- **error** - the error that occurred when fetching the manifest. This will be null if the operation succeeded.

## Deployment 

This Azure function is deployed to https://pwabuilder-manifest-finder.azurewebsites.net