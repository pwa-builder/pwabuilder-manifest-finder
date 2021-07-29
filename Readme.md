## What is this?

This is a lightweight Azure function that analyzes a URL and finds its web manifest.

## How is it used in PWABuilder?

PWABuilder.com uses this service to find a manifest for a PWA. Additionally, if this service is unable to detect a manifest, PWABuilder falls back to a Puppeteer (headless Chrome)-based service to find the manifest.

## Running locally
Open the .sln file in Visual Studio. F5 to run.

## How to use it

Issue a GET to `/api/FindManifest?url=https://somepwa.com`, where somepwa.com is a URL to get the web manifest for. Sample production call:

- https://pwabuilder-manifest-finder.azurewebsites.net/api/findmanifest?url=https://webboard.app

You may optionally supply a ?verbose=1 query string in the URL to return additional error information.

The response will be a JSON object containing:

```typescript
{
    manifestUrl: string | null;
    manifestContents: object | null;
    error: string | null;
    manifestContainsInvalidJson: boolean;
    manifestScore: object | null;
}
```

- **manifestUrl** - the URL to the web manifest. This will be null if there was an error fetching the web app or its manifest.
- **manifestContents** - the manifest object. This will be null if there was an error fetching the web app or its manifest, or if the manifest contents was invalid JSON.
- **error** - the error that occurred when fetching the manifest. This will be null if the operation succeeded.
- **manifestContainsInvalidJson** - If there was an error (error is non-null), this tells you if the error was due to invalid JSON in the manifest.
- **manifestScore** - When the manifest is detected, this contains a score for each property in the manifest.

## Deployment 

This Azure function is deployed to https://pwabuilder-manifest-finder.azurewebsites.net
