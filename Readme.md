## What is this?

This is a lightweight Azure function that analyzes a URL and finds its web manifest.

## How is it used in PWABuilder?

PWABuilder.com uses [Puppeteer](https://developers.google.com/web/tools/puppeteer/), a headless Chrome engine, to load and analyze URLs for web manifest, service worker, HTTPS, and more.

However, when Puppeteer fails to fetch a web app's manifest, we use this function as a fallback.

## Running locally
Open the .sln file in Visual Studio. F5 to run.

## How to use it

Issue a GET to `/api/FindManifest?url=https://somepwa.com`, where somepwa.com is a URL to get the web manifest for.

You may optionally supply a verbose=1 flag in the URL to return verbose error information.

The response will be a JSON object containing:

```typescript
{
    manifestUrl: string | null,
    manifestContents: object | null,
    error: string | null
    manifestContainsInvalidJson: boolean | null;
    manifestScore: object | null;
}
```

- **manifestUrl** - the URL to the web manifest. This will be null if there was an error fetching the web app or its manifest.
- **manifestContents** - the manifest object. This will be null if there was an error fetching the web app or its manifest, or if the manifest contents was invalid JSON.
- **error** - the error that occurred when fetching the manifest. This will be null if the operation succeeded.
- **manifestContainsInvalidJson** - A boolean indicating if the manifest contains invalid JSON.
- **manifestScore** - When the manifest is detected, this contains a score for each property in the manifest.

## Deployment 

This Azure function is deployed to https://pwabuilder-manifest-finder.azurewebsites.net. You can invoke the service using:

- https://pwabuilder-manifest-finder.azurewebsites.net/apiFindManifest?url=https://webboard.app

Where https://webboard.app should be the URL of the PWA you want to detect the manifest for.
