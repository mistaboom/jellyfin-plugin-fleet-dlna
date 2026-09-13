const FleetDlnaConfigurationPage = {
    pluginUniqueId: '17F31D5C-4F2E-4824-903B-759D481B711A',
    defaultDiscoveryInterval: 60,
    defaultAliveInterval: 100,
    loadConfiguration: function (page) {
        return ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then((config) => {
                page.querySelector('#dlnaPlayTo').checked = config.EnablePlayTo;
                page.querySelector('#dlnaDiscoveryInterval').value = parseInt(config.ClientDiscoveryIntervalSeconds, 10) || this.defaultDiscoveryInterval;
                page.querySelector('#dlnaBlastAlive').checked = config.BlastAliveMessages;
                page.querySelector('#dlnaAliveInterval').value = parseInt(config.AliveMessageIntervalSeconds, 10) || this.defaultAliveInterval;
                page.querySelector('#dlnaMatchedHost').checked = config.SendOnlyMatchedHost;

                return ApiClient.getUsers()
                    .then((users) => {
                        this.populateUsers(page, users, config.DefaultUserId);
                    });
            })
            .finally(() => {
                Dashboard.hideLoadingMsg();
            });
    },
    populateUsers: function (page, users, selectedId) {
        let html = '';
        html += '<option value="">None</option>';
        for (let i = 0, length = users.length; i < length; i++) {
            const user = users[i];
            html += '<option value="' + user.Id + '">' + user.Name + '</option>';
        }

        page.querySelector('#dlnaSelectUser').innerHTML = html;
        page.querySelector('#dlnaSelectUser').value = selectedId;
    },
    save: function (page) {
        Dashboard.showLoadingMsg();
        return ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then((config) => {
                config.EnablePlayTo = page.querySelector('#dlnaPlayTo').checked;
                config.ClientDiscoveryIntervalSeconds = parseInt(page.querySelector('#dlnaDiscoveryInterval').value, 10) || this.defaultDiscoveryInterval;
                config.BlastAliveMessages = page.querySelector('#dlnaBlastAlive').checked;
                config.AliveMessageIntervalSeconds = parseInt(page.querySelector('#dlnaAliveInterval').value, 10) || this.defaultAliveInterval;
                config.SendOnlyMatchedHost = page.querySelector('#dlnaMatchedHost').checked;

                const selectedUser = page.querySelector('#dlnaSelectUser').value;
                config.DefaultUserId = selectedUser.length > 0 ? selectedUser : null;

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
