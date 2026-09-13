const FleetDlnaConfigurationPage = {
    pluginUniqueId: '17F31D5C-4F2E-4824-903B-759D481B711A',
    defaultMaximumVideoPageSize: 20,
    defaultStreamPlanningParallelism: 8,
    defaultCountQueryParallelism: 4,
    defaultLatestItemsLimit: 50,
    defaultDiscoveryInterval: 60,
    defaultAliveInterval: 180,
    clampInteger: function (value, fallback, minimum, maximum) {
        const parsedValue = parseInt(value, 10);
        if (Number.isNaN(parsedValue)) {
            return fallback;
        }

        return Math.min(Math.max(parsedValue, minimum), maximum);
    },
    loadConfiguration: function (page) {
        return ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then((config) => {
                page.querySelector('#fleetMaximumVideoPageSize').value = this.clampInteger(config.MaximumVideoPageSize, this.defaultMaximumVideoPageSize, 5, 200);
                page.querySelector('#fleetStreamPlanningParallelism').value = this.clampInteger(config.StreamPlanningParallelism, this.defaultStreamPlanningParallelism, 1, 32);
                page.querySelector('#fleetCountQueryParallelism').value = this.clampInteger(config.CountQueryParallelism, this.defaultCountQueryParallelism, 1, 16);
                page.querySelector('#fleetLatestItemsLimit').value = this.clampInteger(config.LatestItemsLimit, this.defaultLatestItemsLimit, 5, 200);
                page.querySelector('#dlnaPlayTo').checked = config.EnablePlayTo;
                page.querySelector('#dlnaDiscoveryInterval').value = this.clampInteger(config.ClientDiscoveryIntervalSeconds, this.defaultDiscoveryInterval, 10, 3600);
                page.querySelector('#dlnaBlastAlive').checked = config.BlastAliveMessages;
                page.querySelector('#dlnaAliveInterval').value = this.clampInteger(config.AliveMessageIntervalSeconds, this.defaultAliveInterval, 30, 3600);
                page.querySelector('#dlnaMatchedHost').checked = config.SendOnlyMatchedHost;
            })
            .finally(() => {
                Dashboard.hideLoadingMsg();
            });
    },
    save: function (page) {
        Dashboard.showLoadingMsg();
        return ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then((config) => {
                config.MaximumVideoPageSize = this.clampInteger(page.querySelector('#fleetMaximumVideoPageSize').value, this.defaultMaximumVideoPageSize, 5, 200);
                config.StreamPlanningParallelism = this.clampInteger(page.querySelector('#fleetStreamPlanningParallelism').value, this.defaultStreamPlanningParallelism, 1, 32);
                config.CountQueryParallelism = this.clampInteger(page.querySelector('#fleetCountQueryParallelism').value, this.defaultCountQueryParallelism, 1, 16);
                config.LatestItemsLimit = this.clampInteger(page.querySelector('#fleetLatestItemsLimit').value, this.defaultLatestItemsLimit, 5, 200);
                config.EnablePlayTo = page.querySelector('#dlnaPlayTo').checked;
                config.ClientDiscoveryIntervalSeconds = this.clampInteger(page.querySelector('#dlnaDiscoveryInterval').value, this.defaultDiscoveryInterval, 10, 3600);
                config.BlastAliveMessages = page.querySelector('#dlnaBlastAlive').checked;
                config.AliveMessageIntervalSeconds = this.clampInteger(page.querySelector('#dlnaAliveInterval').value, this.defaultAliveInterval, 30, 3600);
                config.SendOnlyMatchedHost = page.querySelector('#dlnaMatchedHost').checked;
                config.DefaultUserId = null;

                return ApiClient.updatePluginConfiguration(this.pluginUniqueId, config);
            })
            .then(Dashboard.processPluginConfigurationUpdateResult)
            .finally(() => {
                Dashboard.hideLoadingMsg();
            });
    }
};

export default function (view) {
    view.querySelector('#dlnaForm').addEventListener('submit', function (e) {
        FleetDlnaConfigurationPage.save(view);
        e.preventDefault();
        return false;
    });

    window.addEventListener('pageshow', function (_) {
        Dashboard.showLoadingMsg();
        FleetDlnaConfigurationPage.loadConfiguration(view);
    });
}
